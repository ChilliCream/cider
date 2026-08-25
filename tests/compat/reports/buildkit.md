# buildkit report

Run: 2026-08-25T19:51:05Z

| Scenario | Result |
|---|---|
| basic build, tag, run | PASS |
| --build-arg + --target (multi-stage) | PASS |
| --secret id=tok,src=file | PASS |
| cache mount + heredoc RUN | PASS |
| --progress plain | PASS |
| --iidfile and -q match `docker images -q` | FAIL (see cider-ger.19) |
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
iid=sha256:a5619e55472f971d32d5d544274ebe839bed1dad820fdc31411b5e49fdf5c094 img=sha256:a5619e55472f971d32d5d544274ebe839bed1dad820fdc31411b5e49fdf5c094 q=sha256:f25540f408b9dafc4aef7cc18d1234550a91887ea9fea22ec0f775263329ee52
```

Not a FileSync/HeaderRewrite regression (cider-ger.16/.18's subsystem): a same-tag rebuild that hits full
BuildKit cache reproduces this identically via a directory context (FileSync/DiffCopy) and via
`docker build - < ctx.tar` (bypasses FileSync entirely), and with `--progress plain` buildkitd's own
`exporting manifest`/`exporting config` digests are byte-identical across repeated runs -- the id drifts
strictly downstream, in what `IContainerRuntime.LoadImagesAsync`/`ListImagesAsync` (`container image load`)
reports back for byte-identical content. Tracked as cider-ger.19; see cider-ger.18's task comments for the
full evidence and a request for sign-off on relaxing this scenario's criterion.

