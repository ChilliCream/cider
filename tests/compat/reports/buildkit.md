# buildkit report

Run: 2026-08-26T11:12:14Z

| Scenario | Result |
|---|---|
| basic build, tag, run | PASS |
| --build-arg + --target (multi-stage) | PASS |
| --secret id=tok,src=file | PASS |
| cache mount + heredoc RUN | PASS |
| --progress plain | PASS |
| --iidfile and -q match `docker images -q` | PASS |
| untagged build is dangling + prunable | PASS |
| --no-cache | PASS |
| --output type=local,dest=<dir> and type=tar,dest=<file> | PASS |
| buildx inspect default --bootstrap, du, prune -f; builder prune -f | PASS |
| compose build (two services, one shared context) | PASS |
| buildx bake (two targets, shared context) | PASS |
| 20 MiB build context within 180s | PASS |

## Apple builder VM survival (cider-ger.3/T4b)

`container builder status` after the run: `running` (PASS)

