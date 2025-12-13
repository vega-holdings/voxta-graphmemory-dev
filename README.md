# GraphMemory Module (Experimental)

This repo tracks experimental work on a graph-backed memory provider for Voxta.

## Current State
- Target: `net10.0`, `Voxta.Sdk.Modules 1.1.4`.
- HuggingFace downloader wired via `Voxta.Shared.HuggingFaceUtils.dll` (local server copy). Config exposes `EmbeddingModel` and `ModelsDirectory`.
- Config surface:
  - `GraphPath` (JSON graph store).
  - `EmbeddingModel`, `ModelsDirectory`.
  - Retrieval knobs: `PrefillMemoryWindow`, `MaxMemoryWindowEntries`, `ExpireMemoriesAfter`, `MaxQueryResults`, `MinScore`, `MaxHops`, `NeighborLimit`, `DeterministicOnly`.
  - Extraction: `ExtractionPromptPath`, `EnablePlaceholderExtraction` (default false).
  - Graph extraction: `GraphExtractionPromptPath`, `EnableGraphExtraction` (default false).
- Prompts (live under `Resources/Prompts/Default/en/GraphMemory/` in the server):
  - `MemoryExtractionSystemMessage.graph.scriban` — lore/summary-style prompt.
  - `GraphExtraction.graph.scriban` — structured JSON prompt for `{entities, relations}`.
  - Editable copies kept in `GraphMemoryArtifacts/`.
- Placeholder summarizer is gated; no raw “Summary:” lore unless explicitly enabled.
- GraphExtractor now tries (best-effort, likely brittle) to resolve `ITextGenService` from DI and call a `GenerateReplyAsync(TextGenGenerateRequest, CancellationToken)` overload via reflection. Most setups won’t expose this; if absent it no-ops. Real LLM wiring still needs a proper module-facing API.

## Repository
Private GitHub: https://github.com/vega-holdings/voxta-graphmemory-dev  
Branch: `master`

## Open Issues / Needs from Voxta Devs
1) **LLM/TextGen access in modules**: Expose a supported interface (e.g., `ITextGenService` or `ISummarizationService`) to modules so we can call the configured model with a custom prompt. Today only null stubs are in `Voxta.Abstractions`; real services are server-internal.
2) **Prompt selection**: Provide a way to supply custom prompts per module/service, rather than hard-coded `PromptTemplates` in `Voxta.Shared.LLMUtils`. Even a file-path override would help.
3) **Graph extraction hook**: If dual extraction is desired (memory lore + graph JSON), support running both in `UpdateMemoryWindowAsync` with separate prompts.
4) **Stable shared libraries**: Publish `Voxta.Shared.HuggingFaceUtils` (and LLMUtils if intended) as packages, or guarantee module-friendly binding redirects.
5) **Testing/inspection**: API/CLI to inspect graph entities/relations/lore for debugging and scripting.

## Next Steps (module side)
- Once an LLM interface is available, wire `GraphExtractor` to:
  - Load `GraphExtractionPromptPath`, inject messages + existing entity names.
  - Call LLM, parse JSON `{entities, relations}`.
  - Dedup and upsert into the graph; optional evidence lore.
- Keep placeholder extraction off by default; rely on LLM outputs.
- Consider embedding real vectors (replace `TextEmbedder`) and scoring blend.

## Caution
- Current graph extraction is a stub; enabling `EnableGraphExtraction` won’t do anything until LLM wiring exists.
- Referencing server-private DLLs (LLMUtils) is brittle; waiting on an official module-facing hook.
