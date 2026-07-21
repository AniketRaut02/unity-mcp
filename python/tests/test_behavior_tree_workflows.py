"""
Phase 5 tests: the workflow registry mechanism plus the Behavior Tree composite
tools built on top of it, run against the fake bridge (extended in Phase 5 to
simulate create_script/update_script/add_component/get_compile_status).
"""
import asyncio
import shutil
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from fake_unity_bridge import run_fake_bridge  # noqa: E402
from unity_mcp_server.bridge_client import UnityBridgeClient  # noqa: E402
from unity_mcp_server import workflows  # noqa: E402


async def main():
    tmp_root = Path(tempfile.mkdtemp())
    port = 46400
    token = "bt-test-token"

    fake_server = await run_fake_bridge(tmp_root, port, token)
    async with fake_server:
        asyncio.get_event_loop().create_task(fake_server.serve_forever())

        client = UnityBridgeClient(project_root=tmp_root)

        # --- Registry sanity ---
        names = {wf.name for wf in workflows.all_workflows()}
        assert names == {
            "batch_execute",
            "manage_tools",
            "scaffold_behavior_tree_framework",
            "create_behavior_tree",
            "add_behavior_tree_node",
        }, names
        print("[PASS] workflow registry contains exactly the expected five tools:", names)

        # --- scaffold_behavior_tree_framework: first call creates all 6 files ---
        scaffold_wf = workflows.get_workflow("scaffold_behavior_tree_framework")
        result1 = await scaffold_wf.handler(client, {})
        assert len(result1["created"]) == 6, result1
        assert result1["skipped"] == [], result1
        print("[PASS] first scaffold call creates all 6 framework files:", result1["created"])

        # --- second call is idempotent: everything already exists, nothing re-created ---
        result2 = await scaffold_wf.handler(client, {})
        assert result2["created"] == [], result2
        assert len(result2["skipped"]) == 6, result2
        print("[PASS] second scaffold call skips all 6 (idempotent, nothing overwritten)")

        # --- create_behavior_tree: framework already scaffolded, so this should build
        # the tree WITHOUT re-scaffolding (frameworkFilesCreated empty) ---
        create_tree_wf = workflows.get_workflow("create_behavior_tree")
        tree_result = await create_tree_wf.handler(
            client,
            {
                "name": "EnemyAI",
                "rootType": "Selector",
                "children": [
                    {
                        "name": "AttackSequence",
                        "type": "Sequence",
                        "children": [
                            {"name": "CheckInRange", "type": "Action"},
                            {"name": "DoAttack", "type": "Action"},
                        ],
                    },
                    {"name": "Idle", "type": "Action"},
                ],
            },
        )
        assert tree_result["frameworkFilesCreated"] == [], tree_result
        assert tree_result["rootPath"] == "EnemyAI", tree_result
        # root + AttackSequence + CheckInRange + DoAttack + Idle = 5 nodes total
        assert len(tree_result["nodes"]) == 5, tree_result
        expected_paths = {
            "EnemyAI",
            "EnemyAI/AttackSequence",
            "EnemyAI/AttackSequence/CheckInRange",
            "EnemyAI/AttackSequence/DoAttack",
            "EnemyAI/Idle",
        }
        assert set(tree_result["nodes"]) == expected_paths, tree_result["nodes"]
        print("[PASS] create_behavior_tree builds the full nested tree with correct hierarchy paths:", tree_result["nodes"])

        # --- add_behavior_tree_node: extends the existing tree ---
        add_node_wf = workflows.get_workflow("add_behavior_tree_node")
        add_result = await add_node_wf.handler(
            client,
            {"parentPath": "EnemyAI", "name": "Flee", "type": "Action"},
        )
        assert add_result["path"] == "EnemyAI/Flee", add_result
        print("[PASS] add_behavior_tree_node extends an existing tree at the right path:", add_result["path"])

        await client.close()

        # --- Fresh project: create_behavior_tree scaffolds the framework itself first,
        # and the compile-wait loop actually polls more than once (fake bridge is
        # configured to report isCompiling=True for 2 polls after a create_script). ---
        tmp_root2 = Path(tempfile.mkdtemp())
        port2 = 46401
        token2 = "bt-test-token-2"
        fake_server2 = await run_fake_bridge(tmp_root2, port2, token2)
        async with fake_server2:
            asyncio.get_event_loop().create_task(fake_server2.serve_forever())
            client2 = UnityBridgeClient(project_root=tmp_root2)

            fresh_result = await create_tree_wf.handler(client2, {"name": "FreshTree", "rootType": "Sequence", "children": []})
            assert len(fresh_result["frameworkFilesCreated"]) == 6, fresh_result
            assert fresh_result["rootPath"] == "FreshTree"
            print("[PASS] create_behavior_tree auto-scaffolds the framework on a fresh project and waits for compile")

            await client2.close()
        fake_server2.close()
        shutil.rmtree(tmp_root2, ignore_errors=True)

        fake_server.close()

    shutil.rmtree(tmp_root, ignore_errors=True)
    print("\nAll Phase 5 workflow/Behavior Tree checks passed.")


if __name__ == "__main__":
    asyncio.run(main())
