"""Verify original V1 data after fixture sync; never writes application data."""
import argparse
import json
import pathlib
from benchmark import digest, read, write

parser = argparse.ArgumentParser()
parser.add_argument("--backup-dir", required=True)
parser.add_argument("--v1-data", required=True)
parser.add_argument("--output", required=True)
args = parser.parse_args()
backup = pathlib.Path(args.backup_dir)
live = pathlib.Path(args.v1_data)
before = read(backup / "v1-knowledge-before.json")
after = read(live / "knowledge.json")
state = read(backup / "state.json")
old_nodes = {node["id"]: node for node in before["nodes"]}
new_nodes = {node["id"]: node for node in after["nodes"]}
changed = [identity for identity, node in old_nodes.items() if identity not in new_nodes or digest(node) != digest(new_nodes[identity])]
expected = {state["sectionId"], *state["documents"].values()}
added = set(new_nodes) - set(old_nodes)
tasks_unchanged = (backup / "v1-tasks-before.json").read_bytes() == (live / "tasks.json").read_bytes()
result = {"originalV1Nodes": len(old_nodes), "currentV1Nodes": len(new_nodes),
          "changedOriginalNodes": len(changed), "addedFixtureNodes": len(added & expected),
          "unexpectedAddedNodes": len(added - expected), "fixtureComplete": expected.issubset(new_nodes),
          "tasksByteIdentical": tasks_unchanged}
write(args.output, result)
print(json.dumps(result))
assert not changed, "Original V1 Knowledge nodes changed"
assert added == expected, "V1 additions differ from fixture manifest"
assert tasks_unchanged, "V1 tasks changed"
