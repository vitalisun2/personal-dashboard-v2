"""Reproducible semantic-search fixture import and real HTTP evaluation (stdlib only).

Judgments stay on disk. The app receives only neutral titles and document bodies.
Use --help; never place state/backup files containing real app data in Git.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import math
import pathlib
import statistics
import subprocess
import time
import urllib.request
import uuid

HERE = pathlib.Path(__file__).resolve().parent
NAMESPACE = uuid.UUID("ec8e6cdb-2813-4097-86aa-d6a6122e412a")
DB_CONTAINER = "personal-os-v2-db-1"
APP_CONTAINER = "personal-os-v2-app-1"


def read(path):
    return json.loads(pathlib.Path(path).read_text(encoding="utf-8-sig"))


def write(path, value):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False).encode()).hexdigest()


def http(base, path, payload=None, timeout=180):
    request = urllib.request.Request(base.rstrip("/") + path,
        data=None if payload is None else json.dumps(payload, ensure_ascii=False).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.load(response)


def shell(args, input=None):
    result = subprocess.run(args, input=input, encoding="utf-8", capture_output=True, check=True)
    return result.stdout.strip()


def db(sql):
    return shell(["docker", "exec", "-i", DB_CONTAINER, "psql", "-X", "-v", "ON_ERROR_STOP=1",
                  "-U", "personal_os_v2", "-d", "personal_os_v2", "-At"], sql)


def literal(value):
    return "'" + str(value).replace("'", "''") + "'"


def configuration():
    info = json.loads(shell(["docker", "inspect", APP_CONTAINER]))[0]
    env = dict(item.split("=", 1) for item in info["Config"]["Env"])
    safe = {key: value for key, value in env.items()
            if key.startswith("V2_SEARCH_") or key in ("OLLAMA_EMBED_MODEL", "OLLAMA_CHAT_MODEL", "OLLAMA_RERANK_MODEL")}
    source_root = HERE.parents[1] / "backend/src/Modules/Search"
    source_hashes = {str(path.relative_to(source_root)): hashlib.sha256(path.read_bytes()).hexdigest()
                     for path in source_root.rglob("*") if path.suffix in (".cs", ".csproj") and not {"bin", "obj"} & set(path.parts)}
    tags = http("http://127.0.0.1:11434", "/api/tags")
    models = {row["name"]: row["digest"] for row in tags.get("models", [])
              if row["name"].split(":")[0] in {safe.get("OLLAMA_EMBED_MODEL", "embeddinggemma").split(":")[0],
                                             safe.get("OLLAMA_CHAT_MODEL", "gemma4").split(":")[0]}}
    return {"environment": safe, "image": info["Image"], "modelDigests": models, "workspaceSearchSourceHashes": source_hashes,
            "gitHead": shell(["git", "rev-parse", "HEAD"]),
            "workingDiffSha256": hashlib.sha256(shell(["git", "diff", "--", "backend/src/Modules/Search", "compose.yml"]).encode()).hexdigest()}


def fixtures():
    lock = read(HERE / "fixtures/fixture-lock.json")
    for name, expected in lock["files"].items():
        assert pathlib.Path(name).name == name, "Invalid fixture filename"
        assert hashlib.sha256((HERE / "fixtures" / name).read_bytes()).hexdigest() == expected, "Frozen fixture changed: " + name
    corpus = read(HERE / "fixtures/corpus.json")
    queries = read(HERE / "fixtures/queries.json")
    ids = [doc["id"] for doc in corpus["documents"]]
    assert len(ids) == len(set(ids)), "Duplicate document IDs"
    qids = [query["id"] for query in queries["queries"]]
    assert len(qids) == len(set(qids)), "Duplicate query IDs"
    for query in queries["queries"]:
        positives = [row["id"] for row in query["relevant"]]
        assert len(positives) == len(set(positives))
        assert set(positives).issubset(ids)
        assert set(query["hardNegatives"]).issubset(ids)
        assert not set(positives) & set(query["hardNegatives"])
    return corpus, queries, digest({"corpus": corpus, "queries": queries})


def utc(value):
    return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))


def seed(args):
    corpus, _, fixture_hash = fixtures()
    section_id = str(uuid.uuid5(NAMESPACE, "v1/section"))
    ids = {doc["id"]: str(uuid.uuid5(NAMESPACE, "v1/" + doc["id"])) for doc in corpus["documents"]}
    owned = {section_id, *ids.values()}
    tree = http(args.base, "/api/v2/knowledge/tree")
    by_id = {node["id"]: node for node in tree}
    state_path = pathlib.Path(args.state)
    state = read(state_path) if state_path.exists() else {
        "sectionId": section_id, "documents": ids, "fixtureHash": fixture_hash,
        "originalNodes": {node["id"]: digest(node) for node in tree if node["id"] not in owned}}
    assert state["fixtureHash"] == fixture_hash, "Fixture changed; use a new explicit dataset version"
    assert state["sectionId"] == section_id and state["documents"] == ids
    write(state_path, state)  # Persist recovery information BEFORE the first mutation.
    payloads = [{"id": section_id, "kind": "section", "title": corpus["sectionTitle"], "parentId": None, "markdown": ""}]
    payloads += [{"id": ids[doc["id"]], "kind": "document", "title": doc["title"],
                  "parentId": section_id, "markdown": doc["body"]} for doc in corpus["documents"]]
    created = 0
    for payload in payloads:
        current = by_id.get(payload["id"])
        if current is not None:
            assert all(current.get(key) == payload[key] for key in ("kind", "title", "parentId", "markdown")), "Existing fixture differs; not overwriting"
        else:
            current = http(args.base, "/api/v2/knowledge/nodes", payload)
            created += 1
        by_id[payload["id"]] = current
    timestamps = [utc(by_id[identity]["updatedAt"]) for identity in ids.values()]
    state.update({"after": (min(timestamps) - dt.timedelta(milliseconds=1)).isoformat(),
                  "before": (max(timestamps) + dt.timedelta(milliseconds=1)).isoformat()})
    write(state_path, state)
    verify(args)
    print(json.dumps({"created": created, "documents": len(ids), "sectionId": section_id}, ensure_ascii=False), flush=True)


def verify(args):
    corpus, _, fixture_hash = fixtures()
    state = read(args.state)
    assert fixture_hash == state["fixtureHash"], "Fixture hash mismatch"
    tree = {node["id"]: node for node in http(args.base, "/api/v2/knowledge/tree")}
    for identity, expected_hash in state["originalNodes"].items():
        assert identity in tree and digest(tree[identity]) == expected_hash, "Original Knowledge node changed: " + identity
    section = tree[state["sectionId"]]
    assert section["kind"] == "section" and section["title"] == corpus["sectionTitle"] and section["parentId"] is None
    for doc in corpus["documents"]:
        node = tree[state["documents"][doc["id"]]]
        assert node["title"] == doc["title"] and node["markdown"] == doc["body"]
        assert node["parentId"] == state["sectionId"] and node["kind"] == "document"
        assert utc(state["after"]) <= utc(node["updatedAt"]) <= utc(state["before"])
    # The current public API has time/kind filters, not section filters. Assert the
    # complete selected source set equals this fixture before treating it as a benchmark.
    selected = set(db("SELECT id FROM search_sources WHERE NOT is_deleted AND kind='knowledge.document' "
                      f"AND updated_at_utc >= {literal(state['after'])}::timestamptz "
                      f"AND updated_at_utc <= {literal(state['before'])}::timestamptz;").splitlines())
    assert selected == set(state["documents"].values()), "Date window is not exactly the fixture; do not benchmark"
    print("Verified fixture content, isolated source set, and unchanged original Knowledge nodes.", flush=True)


def metrics(query, hits):
    expected = {row["id"]: row["grade"] for row in query["relevant"]}
    top = hits[:5]
    found = set(top) & expected.keys()
    precision = len(found) / len(top) if top else (1.0 if not expected else 0.0)
    recall = len(found) / len(expected) if expected else None
    dcg = sum((2 ** expected.get(identity, 0) - 1) / math.log2(rank + 2) for rank, identity in enumerate(top))
    ideal = sum((2 ** grade - 1) / math.log2(rank + 2) for rank, grade in enumerate(sorted(expected.values(), reverse=True)[:5]))
    return {"precisionReturned": precision, "recallAt5": recall,
            "ndcgAt5": dcg / ideal if ideal else None,
            "reciprocalRank": next((1 / (rank + 1) for rank, identity in enumerate(top) if identity in expected), 0.0) if expected else None,
            "noAnswerCorrect": not top if not expected else None,
            "hardNegativeCount": len(set(top) & set(query["hardNegatives"])),
            "falsePositiveCount": len(set(top) - expected.keys())}


def ready(state, config):
    env = config["environment"]
    expected = env.get("OLLAMA_EMBED_MODEL", "embeddinggemma")
    if env.get("V2_SEARCH_PROFILE", "baseline") != "baseline":
        expected += "|retrieval-v1"
    ids = ",".join(literal(identity) for identity in state["documents"].values())
    rows = db("SELECT c.source_id,c.source_version=s.version,c.embedding IS NOT NULL,c.embedding_model "
              "FROM search_chunks c JOIN search_sources s ON c.source_id=s.id AND c.kind=s.kind "
              f"WHERE c.kind='knowledge.document' AND NOT s.is_deleted AND c.source_id IN ({ids});").splitlines()
    parts = [row.split("|", 3) for row in rows]
    assert {row[0] for row in parts} == set(state["documents"].values()), "Some fixture documents have no indexed chunks"
    assert all(row[1] == "t" and row[2] == "t" and row[3] == expected for row in parts), "Stale/incomplete embeddings for current profile; wait for index"
    return expected


def aggregate(rows):
    result = {}
    for split in ("dev", "test", "all"):
        selected = [row for row in rows if split == "all" or row["split"] == split]
        if not selected:
            continue
        values = {"queries": len(selected)}
        for key in ("precisionReturned", "recallAt5", "ndcgAt5", "reciprocalRank", "noAnswerCorrect"):
            samples = [row["metrics"][key] for row in selected if row["metrics"][key] is not None]
            values[key] = statistics.mean(samples) if samples else None
        values["hardNegatives"] = sum(row["metrics"]["hardNegativeCount"] for row in selected)
        values["falsePositives"] = sum(row["metrics"]["falsePositiveCount"] for row in selected)
        times = sorted(row["elapsedMs"] for row in selected if "elapsedMs" in row)
        if times:
            values["medianMs"] = statistics.median(times)
            values["p95Ms"] = times[max(0, math.ceil(len(times) * .95) - 1)]
        values["fallbacks"] = sum(bool(row.get("fallback")) for row in selected)
        result[split] = values
    return result


def run(args):
    verify(args)
    state = read(args.state)
    _, suite, fixture_hash = fixtures()
    reverse = {value: key for key, value in state["documents"].items()}
    result = {"label": args.label, "startedUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
              "fixtureHash": fixture_hash, "configuration": configuration(), "rows": []}
    result["embeddingIdentity"] = ready(state, result["configuration"])
    for query in suite["queries"]:
        if args.split != "all" and query["split"] != args.split:
            continue
        payload = {"query": query["query"], "mode": "relevant", "matchMode": "semantic", "pageSize": 5,
                   "kinds": ["knowledge.document"], "updatedAfterUtc": state["after"], "updatedBeforeUtc": state["before"]}
        started = time.perf_counter()
        response = http(args.base, "/api/v2/search", payload)
        elapsed = round((time.perf_counter() - started) * 1000, 1)
        ids = [reverse.get(hit["source"]["id"], "EXTERNAL") for hit in response["hits"]]
        assert "EXTERNAL" not in ids, "API filter leaked unrelated data"
        note = response.get("coverageNote") or ""
        unavailable = "смысловой поиск временно недоступен" in note.lower() or "индекс ещё" in note.lower()
        measured = metrics(query, ids)
        if unavailable and not query["relevant"]:
            measured["noAnswerCorrect"] = False
            measured["precisionReturned"] = 0.0
        row = {"id": query["id"], "split": query["split"], "query": query["query"],
               "expected": query["relevant"], "actual": ids,
               "similarities": [hit.get("semanticSimilarity") for hit in response["hits"]],
               "elapsedMs": elapsed, "coverageNote": note,
               "unavailable": unavailable,
               "fallback": any(word in note.lower() for word in ("недоступ", "не заверш", "ошиб", "fallback", "резерв", "не удалось")),
               "metrics": measured}
        result["rows"].append(row)
        result["summary"] = aggregate(result["rows"])
        write(args.output, result)
        print(f"{args.label} {query['id']}: {','.join(ids) or 'empty'} ({elapsed:.0f} ms)", flush=True)
    print(json.dumps(result["summary"], ensure_ascii=False), flush=True)


def scores(args):
    """Read the real chunk vectors and collect dev-only candidate scores for threshold selection."""
    verify(args)
    state = read(args.state)
    _, suite, fixture_hash = fixtures()
    reverse = {value: key for key, value in state["documents"].items()}
    config = configuration()
    ready(state, config)
    env = config["environment"]
    model = env.get("OLLAMA_EMBED_MODEL", "embeddinggemma")
    prompted = env.get("V2_SEARCH_PROFILE", "baseline") != "baseline"
    model_identity = model + ("|retrieval-v1" if prompted else "")
    result = {"fixtureHash": fixture_hash, "configuration": config, "rows": []}
    for query in suite["queries"]:
        if query["split"] != "dev":
            continue  # Never tune thresholds on held-out queries.
        text = ("task: search result | query: " if prompted else "") + query["query"]
        vector = http(args.ollama, "/api/embed", {"model": model, "input": [text]})["embeddings"][0]
        vector_literal = literal("[" + ",".join(str(value) for value in vector) + "]")
        sql = f"SELECT source_id, max(1-(embedding <=> {vector_literal}::vector)) AS score FROM search_chunks "
        sql += "WHERE source_id IN (" + ",".join(literal(identity) for identity in reverse) + ") "
        sql += f"AND embedding_model = {literal(model_identity)} GROUP BY source_id ORDER BY score DESC,source_id;"
        matches = [{"id": reverse[line.split("|")[0]], "score": float(line.split("|")[1])} for line in db(sql).splitlines()]
        assert len(matches) == len(reverse), "Missing document vectors"
        result["rows"].append({"id": query["id"], "candidates": matches})
        write(args.output, result)
        print("Scored " + query["id"], flush=True)
    trials = []
    lookup = {row["id"]: row for row in suite["queries"]}
    for threshold in [round(value / 100, 2) for value in range(25, 76, 2)]:
        rows = []
        for row in result["rows"]:
            query = lookup[row["id"]]
            ids = [candidate["id"] for candidate in row["candidates"] if candidate["score"] >= threshold][:5]
            rows.append({"split": "dev", "metrics": metrics(query, ids)})
        summary = aggregate(rows)["dev"]
        precision, recall = summary["precisionReturned"], summary["recallAt5"]
        # Predeclared equal weighting of returned-result precision and positive-query recall.
        utility = 2 * precision * recall / (precision + recall) if precision + recall else 0
        trials.append({"threshold": threshold, "utility": utility, **summary})
    result["thresholdTrials"] = sorted(trials, key=lambda trial: (-trial["utility"], -trial["ndcgAt5"], trial["threshold"]))
    write(args.output, result)
    print(json.dumps(result["thresholdTrials"][:5], ensure_ascii=False), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("seed", "verify", "run", "scores"))
    parser.add_argument("--base", default="http://127.0.0.1:8090")
    parser.add_argument("--ollama", default="http://127.0.0.1:11434")
    parser.add_argument("--state", required=True, help="Private manifest path, outside the repository")
    parser.add_argument("--label", default="baseline")
    parser.add_argument("--split", choices=("dev", "test", "all"), default="dev")
    parser.add_argument("--output", default=str(HERE / "results/run.json"))
    args = parser.parse_args()
    globals()[args.command](args)


if __name__ == "__main__":
    main()
