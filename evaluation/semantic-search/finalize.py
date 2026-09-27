"""Combine measured runs without changing any frozen judgments."""
from benchmark import HERE, aggregate, read, write

for profile in ("baseline", "prompted", "reranked"):
    paths = [HERE / "results" / f"{profile}-{split}.json" for split in ("dev", "test")]
    runs = [read(path) for path in paths if path.exists()]
    rows = [row for run in runs for row in run["rows"]]
    assert len({row["id"] for row in rows}) == len(rows)
    assert len({run["fixtureHash"] for run in runs}) == 1
    label = profile if len(rows) == 36 else f"{profile} — частично ({len(rows)}/36)"
    write(HERE / "results" / f"{profile}.json", {
        "label": label, "profile": profile, "complete": len(rows) == 36,
        "fixtureHash": runs[0]["fixtureHash"], "rows": rows, "summary": aggregate(rows),
        "sourceRuns": [{key: value for key, value in run.items() if key not in ("rows", "summary")} for run in runs]})

reranked = read(HERE / "results/reranked.json")
selected_ids = {row["id"] for row in reranked["rows"]}
matched = {}
for profile in ("baseline", "prompted", "reranked"):
    run = read(HERE / "results" / f"{profile}.json")
    matched[profile] = aggregate([row for row in run["rows"] if row["id"] in selected_ids])["all"]
write(HERE / "results/same-query-comparison.json", {"queryIds": sorted(selected_ids), "profiles": matched,
      "note": "Only the identical completed subset; Gemma experiment stopped at user request. No claim of full held-out evaluation for Gemma."})
for profile in ("baseline", "prompted"):
    result = read(HERE / "results" / f"{profile}.json")
    print(profile, result["summary"]["test"])
print("Matched subset:", matched)
