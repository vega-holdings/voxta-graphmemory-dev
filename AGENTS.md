# Graph Memory Module — Agent Notes

## Current State
- Project: `Voxta.Modules.GraphMemory` (net10.0, Voxta.Sdk.Modules 1.1.4).
- Registration: experimental, single memory provider.
- Config: graph path, embedding model (now wired to HuggingFace downloader), models directory, optional extraction prompt path, placeholder extraction toggle, prefill/window/expiry, max query results, min score, max hops, neighbor limit, deterministic flag.
- Graph storage: JSON-backed `GraphStore` with entities/relations/lore persisted to disk.
- Mapping: helpers to map graph items to `MemoryRef` (with rough token estimates).
- Provider instance:
  - Persists lore (from `MemoryRef` updates) into the graph store.
  - Prefill uses `MemoryPrefillHelper`.
  - UpdateMemoryWindow: extracts terms from new messages, searches graph (keyword-based), expands neighbors (hops/limit), ranks, and inserts within token/window caps.
  - Placeholder auto-lore is now gated behind `EnablePlaceholderExtraction` (default false) to avoid dumping raw chat; prompt path is configurable but not yet used by a real extractor.
  - SearchAsync: returns ranked matches (keyword-based).
- No embeddings yet; deterministic keyword-only retrieval.

## Immediate Next Steps
1) Embeddings:
   - Replace placeholder `TextEmbedder` with real MSK SentenceTransformers (Voxta.Shared.HuggingFaceUtils + SK vector store or MSK embedder).
   - Persist vectors on entities/relations/lore; re-embed on add/update.
   - Apply `MinScore` using true cosine similarity.
2) Retrieval refinement:
   - Scoring blend: similarity + weight + recency; enforce token budget by dropping lowest-score overflow.
   - Respect `MaxQueryResults` when selecting candidates; cap neighbor expansion by score/limit.
3) Agentic extraction:
   - Swap placeholder summary lore for LLM-based entity/relation extraction and lore synthesis (reuse Voxta summarization/extraction pipeline if accessible). Stub plumbing added: configurable graph extraction prompt + toggle, prompt files dropped.
   - Dedup by name/alias similarity and relation signatures before upserting to graph.
   - Use configurable prompt path (default `Resources/Prompts/Default/en/GraphMemory/MemoryExtractionSystemMessage.graph.scriban`); add real LLM call to replace placeholder.
4) Export/import:
   - Add helper/command to dump/load graph JSON for backup/migration.
5) Observability:
   - Add logging around matches, prunes, oversize items; counters for added/updated/pruned; debug traces for query → results.
6) Deployment:
   - Ensure module loads in a full Voxta server environment (with Serilog config/deps); test end-to-end once sandbox restrictions are lifted.

## Nice-to-Have (Later)
- Backend abstraction for alternative graph stores (Neo4j/RedisGraph).
- UI/CLI inspector for entities/relations/lore.
- Multi-hop expansion with tighter limits; deterministic keyword-only mode toggle.

## Current Findings (Dec 12, 2025)
- HuggingFace downloader is available via `Voxta.Shared.HuggingFaceUtils.dll` in the server root; GraphMemory now references it and exposes `EmbeddingModel` + `ModelsDirectory` like the SK module.
- Added config fields for `ExtractionPromptPath` and `EnablePlaceholderExtraction` (defaults to GraphMemory prompt path and false). Placeholder summaries are suppressed unless explicitly enabled.
- Custom prompts dropped at `Resources/Prompts/Default/en/GraphMemory/MemoryExtractionSystemMessage.graph.scriban` (lore) and `GraphExtraction.graph.scriban` (structured entities/relations); no runtime LLM wiring yet—GraphExtractor stub builds the prompt but returns null.
- Graph extraction config fields added: `GraphExtractionPromptPath`, `EnableGraphExtraction` (defaults off). Placeholder remains off by default.
- The shipped server summarization prompt (`Resources/Prompts/Default/en/Summarization/MemoryExtractionSystemMessage.scriban`) is hard-coded in `Voxta.Shared.LLMUtils.Prompting.PromptTemplates`; there’s no SDK knob to swap prompts. Changing prompts today requires file replacement or a custom extractor.
- MicrosoftSemanticKernel module does not generate summaries; it only indexes provided `MemoryRef` items. The 1996 lore entries in `graphs/graph-memory.db` came from the old placeholder summarizer, not SK.

## Current Findings (Dec 13, 2025)
- Implemented `GRAPH_JSON:` parsing in `Memory/GraphExtractor.TryParseGraphFromText(...)`.
- `GraphMemoryProviderInstance` now calls `ProcessGraphFromMemoryRef(...)` during `RegisterMemoriesAsync`/`UpdateMemoriesAsync` to upsert entities/relations when `EnableGraphExtraction` is enabled.
- Practical POC path: override the server’s `Resources/Prompts/Default/en/Summarization/MemoryExtractionSystemMessage.scriban` to emit a `GRAPH_JSON:` line; GraphMemory will ingest it from memory items and build the graph.

## Current Findings (Dec 13, 2025 — later)
- `GraphExtractionTrigger` config added:
  - `OnlyOnMemoryGeneration` (default): GraphMemory only parses `GRAPH_JSON:` blocks from memory items.
  - `EveryTurn`: GraphMemory calls the currently-selected `ITextGenService` via `IDynamicServiceAccessor<ITextGenService>` using `GraphExtractionPromptPath` and applies entities/relations in the background.
- `GRAPH_JSON:`-only memory items are not stored as graph lore (they’re parsed only) to avoid polluting retrieval.
