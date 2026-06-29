# Generated knowledge graph artifacts

This folder is populated by:

```powershell
pwsh .\tools\knowledge-graph\New-ChairSideKnowledgeGraph.ps1
```

Generated files are mechanical aids. They do not replace the hand-authored graph in `../chairside.graph.md`.

Expected outputs:

- `file-inventory.md` - repo files grouped into a reviewable table with discovered symbols.
- `symbol-index.json` - extracted code symbols, routes, CSS variables, and script functions.
- `graph-data.json` - simple node/edge data that future tooling can consume.

Review generated diffs before committing. If the output is noisy, tighten the generator instead of accepting a low-signal graph.
