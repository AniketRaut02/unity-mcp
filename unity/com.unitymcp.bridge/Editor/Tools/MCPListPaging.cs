using System.Collections.Generic;
using System.Linq;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Summary-first paging for tools that return path lists (list_assets, list_scripts,
    /// find_gameobjects): small results come back whole; big ones come back as a total, a
    /// per-top-level-folder count, and one page, so a 5,000-asset project doesn't cost tens of
    /// thousands of tokens on the first look.
    /// </summary>
    internal static class MCPListPaging
    {
        public const int DefaultLimit = 100;

        /// <param name="itemsKey">Result key for the page itself ("assets", "scripts", "paths"), unchanged from before paging existed.</param>
        /// <param name="limit">Page size; 0 or less means everything.</param>
        public static Dictionary<string, object> Page(IReadOnlyList<string> items, string itemsKey, int offset, int limit)
        {
            offset = System.Math.Max(0, offset);
            var page = limit > 0 ? items.Skip(offset).Take(limit).ToList() : items.Skip(offset).ToList();
            bool truncated = offset + page.Count < items.Count;

            var result = new Dictionary<string, object>
            {
                ["total"] = items.Count,
                ["offset"] = offset,
                ["returned"] = page.Count,
                [itemsKey] = page
            };

            if (truncated)
            {
                result["truncated"] = true;
                result["byFolder"] = items
                    .GroupBy(p => p.Contains("/") ? p.Substring(0, p.IndexOf('/')) : "(top level)")
                    .OrderByDescending(g => g.Count())
                    .Take(30)
                    .ToDictionary(g => g.Key, g => g.Count());
                result["hint"] = $"Showing {page.Count} of {items.Count}. Narrow the search (byFolder shows where they are) " +
                                 $"or page with offset={offset + page.Count}; limit=0 returns everything.";
            }

            return result;
        }
    }
}
