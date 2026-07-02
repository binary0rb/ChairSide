@AGENTS.md

## Claude Code note

Use AGENTS.md as the canonical ChairSide project instructions. Do not duplicate or fork those instructions here.

Before broad source inspection, consult the private development knowledge graph described in AGENTS.md and use it to identify the smallest relevant file set. Do not scan the whole repository unless the graph is insufficient or the task explicitly requires whole-repo review.

Do not import generated graph JSON into CLAUDE.md.

Keep committed Markdown/docs ASCII-safe (see AGENTS.md, Markdown and documentation formatting). No smart quotes, en/em dashes, ellipsis characters, section symbols, or multiplication signs. Before finishing a Markdown edit, run Select-String -Pattern "[^\x00-\x7F]" -AllMatches on the changed file and fix any hits.
