"""
BM25 keyword search over the full tool catalog (atomic Unity tools + composite Python
tools + group descriptions), independent of which groups are currently active -- see
docs/tool-scaling-strategy.md for the full design rationale.

Deliberately no embeddings model: this corpus is a few hundred short, keyword-dense,
human-written documents (see docs/tool-catalog.md's own writing convention), well within
the range where plain BM25 gets results close to an embeddings model for the query
patterns an agent actually uses ("terrain sculpting", "flickering light scare") -- these
are keyword-rich, not paraphrase-heavy -- at zero dependency/startup/network cost. The
same reasoning Anthropic gives for offering a BM25 variant of their own Tool Search Tool
alongside a regex variant, as "simpler for teams without regex knowledge".

Rebuilt fresh on every search() call rather than cached: the corpus (a few hundred
documents) is cheap enough to index in well under a second, and rebuilding fresh means
there is no cache-invalidation logic to get wrong if the tool catalog ever changes
mid-session (a Unity domain reload adding/removing an [MCPTool] method).
"""
import math
import re
from dataclasses import dataclass, field
from typing import Optional

_TOKEN_RE = re.compile(r"[a-z0-9]+")

# Standard BM25 constants -- see docs/tool-scaling-strategy.md's "Decisions" section:
# shipped with textbook defaults, to be tuned later against real usage rather than
# researched up front.
_K1 = 1.5
_B = 0.75

# Multi-field weights: a query matching the tool's own NAME should always outrank one
# that only matches a parameter description.
_FIELD_WEIGHTS = {"name": 3.0, "group": 2.0, "description": 1.0, "params": 0.5}

# Added to a candidate's BM25 score when the query is a direct substring of the tool's
# own name (underscores treated as spaces) -- covers the common case where the model
# already half-remembers a tool's name from an earlier turn or from training data, which
# pure term-frequency scoring alone doesn't reliably surface at the very top.
_EXACT_NAME_SUBSTRING_BOOST = 10.0


def _stem(word: str) -> str:
    """
    Minimal, dependency-free suffix stripping -- not a real Porter stemmer, just enough
    to close the most common word-form gap between a natural-language query and a tool's
    exact naming (e.g. query "flickering light" must match a tool named
    "add_flicker_light"; without this, "flickering" and "flicker" are simply different
    tokens and never match at all). Verified against this exact case -- see
    test_tool_search.py.
    """
    if len(word) > 6 and word.endswith("ing"):
        return word[:-3]
    if len(word) > 5 and word.endswith("ed"):
        return word[:-2]
    if len(word) > 5 and word.endswith("es"):
        return word[:-2]
    if len(word) > 4 and word.endswith("s") and not word.endswith("ss"):
        return word[:-1]
    return word


def _tokenize(text: str) -> list[str]:
    return [_stem(w) for w in _TOKEN_RE.findall(text.lower())]


@dataclass
class ToolDoc:
    """One searchable entry. `name=None` marks a group-level pseudo-document (the
    group's own description, not a specific tool) -- see ToolSearchIndex's docstring."""
    group: str
    description: str
    name: Optional[str] = None
    param_text: str = ""
    is_composite: bool = False


@dataclass
class _IndexedDoc:
    doc: ToolDoc
    field_tokens: dict = field(default_factory=dict)
    weighted_length: float = 0.0


class ToolSearchIndex:
    """
    Indexes both individual tools AND each group's own description as a pseudo-document
    (ToolDoc with name=None) -- so a broad query like "how do I set up a scare sequence"
    can surface the `timeline` group even when no individual tool name contains those
    words, per docs/tool-scaling-strategy.md section 5.
    """

    def __init__(self, docs: list[ToolDoc]):
        self._indexed: list[_IndexedDoc] = []
        self._doc_freq: dict[str, int] = {}

        total_weighted_length = 0.0
        for doc in docs:
            fields = {
                "name": _tokenize((doc.name or "").replace("_", " ")),
                "group": _tokenize(doc.group.replace("_", " ")),
                "description": _tokenize(doc.description),
                "params": _tokenize(doc.param_text),
            }
            weighted_length = sum(len(toks) * _FIELD_WEIGHTS[f] for f, toks in fields.items())
            total_weighted_length += weighted_length
            self._indexed.append(_IndexedDoc(doc=doc, field_tokens=fields, weighted_length=weighted_length))

            unique_terms = {t for toks in fields.values() for t in toks}
            for t in unique_terms:
                self._doc_freq[t] = self._doc_freq.get(t, 0) + 1

        self._n_docs = len(docs)
        self._avg_weighted_length = total_weighted_length / self._n_docs if self._n_docs else 1.0

    def _idf(self, term: str) -> float:
        n = self._doc_freq.get(term, 0)
        # +1 smoothing keeps this non-negative even for a term that appears in most docs.
        return math.log(1 + (self._n_docs - n + 0.5) / (n + 0.5))

    def _score(self, indexed: _IndexedDoc, query_terms: list[str]) -> float:
        term_weight: dict[str, float] = {}
        for f, toks in indexed.field_tokens.items():
            w = _FIELD_WEIGHTS[f]
            for t in toks:
                term_weight[t] = term_weight.get(t, 0.0) + w

        score = 0.0
        length_norm = 1 - _B + _B * indexed.weighted_length / self._avg_weighted_length
        for term in query_terms:
            tf = term_weight.get(term, 0.0)
            if tf == 0.0:
                continue
            idf = self._idf(term)
            score += idf * (tf * (_K1 + 1)) / (tf + _K1 * length_norm)
        return score

    def search(self, query: str, limit: int = 8) -> list[ToolDoc]:
        query_terms = _tokenize(query)
        if not query_terms:
            return []
        query_normalized = " ".join(query_terms)

        scored = []
        for indexed in self._indexed:
            score = self._score(indexed, query_terms)

            name_normalized = (indexed.doc.name or "").replace("_", " ")
            if name_normalized and query_normalized in name_normalized:
                score += _EXACT_NAME_SUBSTRING_BOOST

            if score > 0:
                scored.append((score, indexed.doc))

        scored.sort(key=lambda pair: pair[0], reverse=True)
        return [doc for _, doc in scored[:limit]]
