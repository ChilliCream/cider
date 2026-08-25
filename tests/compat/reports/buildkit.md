# buildkit report

Run: 2026-08-25T19:26:18Z

| Scenario | Result |
|---|---|
| basic build, tag, run | PASS |
| --build-arg + --target (multi-stage) | PASS |
| --secret id=tok,src=file | PASS |
| cache mount + heredoc RUN | PASS |
| --progress plain | PASS |
| --iidfile and -q match `docker images -q` | FAIL |
| untagged build is dangling + prunable | PASS |
| --no-cache | PASS |
| --output type=local,dest=<dir> and type=tar,dest=<file> | PASS |
| buildx inspect default --bootstrap, du, prune -f; builder prune -f | PASS |
| compose build (two services, one shared context) | PASS |
| buildx bake (two targets, shared context) | PASS |
| 20 MiB build context within 180s | PASS |

## Apple builder VM survival (cider-ger.3/T4b)

`container builder status` after the run: `running` (PASS)

### --iidfile and -q match `docker images -q`

```
iid=sha256:2b4c0f2709b36b67d804fca8107044c8c49ec46573ab6e6e2b89699d6a8b0b9b img=sha256:2b4c0f2709b36b67d804fca8107044c8c49ec46573ab6e6e2b89699d6a8b0b9b q=sha256:4def51751f186cb67e58cf8fe118c2b7ce54d695c2aea50d3e37af959e0d6f3d
```

