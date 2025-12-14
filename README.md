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
  - Graph extraction: `GraphExtractionPromptPath`, `EnableGraphExtraction` (default true).
  - Graph extraction trigger: `GraphExtractionTrigger` (`OnlyOnMemoryGeneration` default, or `EveryTurn`).
- Prompts (live under `Resources/Prompts/Default/en/GraphMemory/` in the server):
  - `MemoryExtractionSystemMessage.graph.scriban` — lore/summary-style prompt.
  - `GraphExtraction.graph.scriban` — structured JSON prompt for `{entities, relations}`.
  - Editable copies kept in `GraphMemoryArtifacts/`.
- Placeholder summarizer is gated; no raw “Summary:” lore unless explicitly enabled.
- GraphMemory supports two ways to populate the graph:
  - `OnlyOnMemoryGeneration` (default): parse `GRAPH_JSON:` blocks from incoming `MemoryRef.Text` (requires the summarization/memory prompt to emit them).
  - `EveryTurn`: call the currently-selected `ITextGenService` via `IDynamicServiceAccessor<ITextGenService>` and run `GraphExtractionPromptPath` on each batch of new messages (runs in the background; updates affect future turns).

## Recommended Integration (YOLOLLM + GraphMemory)
If you’re using `Voxta.Modules.YoloLLM`, the intended pairing is `GraphMemory`:
1) Enable `YoloLLM` for Summarization (it runs a **separate** graph extraction LLM call during summarization and queues a `GRAPH_JSON:` memory item).
2) Enable `GraphMemory` as the memory provider with `EnableGraphExtraction=true` and `GraphExtractionTrigger=OnlyOnMemoryGeneration` so it ingests `GRAPH_JSON:` memory items and updates the graph DB.

This keeps **graph extraction separate from summary/memory extraction prompts**, while still using the memory pipeline to “deliver” the graph JSON to GraphMemory.

Notes:
- YOLOLLM injects `meta.chatId/sessionId/user/characters` into `GRAPH_JSON:` so GraphMemory can scope graph writes for group chats.
- GraphMemory search prefers in-scope items (`chatId` match) but still allows global items (`chatId` missing).

## Legacy Graph JSON Approach (POC)
The simplest “no extra LLM calls” approach was:
1) Make the server’s memory-extraction prompt emit a `GRAPH_JSON:` line (global file override).
2) Let GraphMemory parse that `GRAPH_JSON:` from the resulting memory items and upsert the graph.

In this workspace, the overridden prompt is:
- `Voxta.Server.Win.v1.2.0/Resources/Prompts/Default/en/Summarization/MemoryExtractionSystemMessage.scriban`

Notes:
- This is a hack and applies globally to the server prompt set.
- Depending on how the server parses memory extraction output, the `GRAPH_JSON:` line may also end up as a stored memory/lore entry.
 - GraphMemory will *not* store `GRAPH_JSON:`-only memory items as graph lore (it only parses them to upsert entities/relations).

## Repository
Private GitHub: https://github.com/vega-holdings/voxta-graphmemory-dev  
Branch: `master`

## Open Issues / Needs from Voxta Devs
1) **Supported “call current LLM” hook**: In server v1.2.0 we can use `IDynamicServiceAccessor<ITextGenService>`, but this isn’t documented as stable SDK surface. A supported module API to invoke the configured LLM with a custom prompt would remove guesswork.
2) **Prompt selection**: Provide a way to supply custom prompts per module/service, rather than hard-coded `PromptTemplates` in `Voxta.Shared.LLMUtils`. Even a file-path override would help.
3) **Graph extraction hook**: If dual extraction is desired (memory lore + graph JSON), support running both in `UpdateMemoryWindowAsync` with separate prompts.
4) **Stable shared libraries**: Publish `Voxta.Shared.HuggingFaceUtils` (and LLMUtils if intended) as packages, or guarantee module-friendly binding redirects.
5) **Testing/inspection**: API/CLI to inspect graph entities/relations/lore for debugging and scripting.

## Next Steps (module side)
- Keep placeholder extraction off by default; rely on LLM outputs.
- Consider embedding real vectors (replace `TextEmbedder`) and scoring blend.

## Caution
- `GraphExtractionTrigger=EveryTurn` runs an extra LLM call per turn (cost/latency) and is executed in the background; failures are logged and skipped.
- Referencing server-private DLLs (LLMUtils) is brittle; waiting on an official module-facing hook.


![Gemini_Generated_Image_te9i0te9i0te9i0t](https://github.com/user-attachments/assets/f01f25d1-b4f9-4261-b3e6-efdefa9f8ebb)
