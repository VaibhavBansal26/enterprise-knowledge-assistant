# 0003. LLM and embeddings behind interfaces, with offline stand-ins
**Status:** accepted

**Context.** Tests, CI and local development should not need API keys or network access, and providers change.

**Decision.** `ILlmClient` and `IEmbeddingClient` are the seams. Real implementations: the official Anthropic SDK (with a circuit breaker, a first-event timeout and refusal fallback to a second model) and an OpenAI-compatible embeddings client. Defaults are deterministic offline stand-ins (`extractive-fallback`, `hashing-v2`).

**Consequences.** The whole stack, evals included, runs offline and deterministically. Eval scores from the stand-ins do not predict real-model quality; run the same golden set against a real provider before trusting a model change. Changing the stand-in embedding algorithm needs a new model id and reprocessing documents.
