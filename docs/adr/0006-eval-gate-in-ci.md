# 0006. Quality evals gate CI with a committed baseline
**Status:** accepted

**Context.** RAG quality and safety regress silently when prompts, retrieval or thresholds change.

**Decision.** `evals/golden.json` runs in CI. Hard gates (leaks, injections, PII exposure) must be zero; each metric has a floor; and no metric may drop more than 0.02 below `evals/baseline.json`. Intentional changes update the baseline in the same PR, so the change is reviewed.

**Consequences.** Regressions fail the build with a readable report. The set is small and was tuned alongside the stand-ins, so it catches regressions rather than proving quality; grow it from real unanswered or downvoted questions.
