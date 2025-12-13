# GraphMemory Prompts and Templates

This folder keeps editable copies of the GraphMemory prompts so they can be versioned and tweaked without hunting through the server bundle.

Files:
- `MemoryExtractionSystemMessage.graph.scriban` — Lore/summary-style extraction prompt (GraphMemory-specific variant).
- `GraphExtraction.graph.scriban` — Structured graph extraction prompt that asks for JSON `{entities, relations}`.

Deployment locations:
- Live copies are expected at `Voxta.Server.Win.v1.2.0/Resources/Prompts/Default/en/GraphMemory/`.

Notes:
- Graph extraction is currently stubbed in code (no LLM call yet); prompt here is ready for when an LLM interface is available.
- Placeholder “Summary:” auto-lore is disabled by default; enable only if you want deterministic text dumps.
