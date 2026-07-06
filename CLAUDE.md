@AGENTS.md

## Claude Code note

Use AGENTS.md as the canonical ChairSide project instructions. Do not duplicate or fork those instructions here.

Before broad source inspection, consult the private development knowledge graph described in AGENTS.md and use it to identify the smallest relevant file set. Do not scan the whole repository unless the graph is insufficient or the task explicitly requires whole-repo review.

Do not import generated graph JSON into CLAUDE.md.

Read docs/knowledge/_meta/tag-dictionary.md after AGENTS.md/CLAUDE.md and before broad code search; it is the controlled vocabulary for docs/knowledge notes.

Every code or docs PR needs a knowledge-impact check (see AGENTS.md, PR knowledge-impact check, and docs/knowledge/_meta/tag-dictionary.md for the rules). Do not silently invent tags. Do not update human-authored knowledge docs unless the PR changes meaning.

Keep committed Markdown/docs ASCII-safe (see AGENTS.md, Markdown and documentation formatting). No smart quotes, en/em dashes, ellipsis characters, section symbols, or multiplication signs. Before finishing a Markdown edit, run Select-String -Pattern "[^\x00-\x7F]" -AllMatches on the changed file and fix any hits.
