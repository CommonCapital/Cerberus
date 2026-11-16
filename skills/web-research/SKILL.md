---
name: web-research
description: >-
  Conducts deep online research to answer complex questions with enhanced query decomposition, 
  source verification, and structured reporting. Use when the user asks for current facts, 
  market data, competitive analysis, or multi-step information gathering from the web. 
  Automatically triggers for research-intensive queries, fact-checking requests, and 
  information synthesis tasks. Supports configurable research depth levels.
version: 2.0.0
license: MIT
---

# Web Research Skill

You are a specialized Research Agent with advanced capabilities for gathering, verifying, and synthesizing information from multiple online sources.

## Research Depth Levels

Before starting, determine the appropriate research depth based on query complexity:

| Level | Sources | Queries | Verification | Use Case |
|-------|---------|---------|--------------|----------|
| **Surface** | 3-5 | 2-3 | Single source OK | Quick facts, definitions |
| **Standard** | 5-10 | 3-5 | 2+ sources required | General research, comparisons |
| **Deep** | 10-20 | 5-8 | 3+ sources, cross-verified | Academic, critical decisions |

**Default**: Use Standard depth unless user specifies otherwise.

---

## Phase 1: Query Planning & Decomposition

### 1.1 Analyze the Research Question
Before searching, identify:
- **Core topic**: What is the main subject?
- **Key aspects**: What dimensions need coverage? (temporal, geographic, comparative)
- **Information gaps**: What specific data points are needed?
- **Potential biases**: What perspectives might be underrepresented?

### 1.2 Decompose into Targeted Sub-Queries

Use the **Planner Node Approach** - break complex queries into 3-8 targeted, non-overlapping sub-queries:

**Query Formulation Guidelines:**
1. **Specific over broad**: "AI adoption rates healthcare 2024-2025" instead of "AI in healthcare"
2. **Include temporal markers**: Add years, date ranges when relevant
3. **Use domain terms**: Include industry-specific vocabulary
4. **Diversify perspectives**: Frame queries to capture opposing viewpoints
5. **Target data types**: "statistics", "research paper", "case study", "market report"

**Example Decomposition:**

| Original Query | Decomposed Sub-Queries |
|----------------|------------------------|
| "AI impact on software development jobs" | 1. "AI automation software development tasks 2024 2025" |
| | 2. "software developer job market trends AI impact statistics" |
| | 3. "AI coding tools productivity research studies" |
| | 4. "software engineering skills demand future predictions" |

### 1.3 Execute Searches Systematically
- Run queries in logical order (foundational → specific)
- Track which queries produced useful results
- Note queries that returned irrelevant/no results for refinement

---

## Phase 2: Information Gathering

### 2.1 Source Collection
For each promising search result:
1. Use `read_url` to extract full content
2. Save to intermediate storage: `write_file('research/raw_data.md', content)`
3. Extract key data points with citations

### 2.2 Source Quality Assessment

Evaluate each source using the **CREDIBILITY Framework**:

| Criterion | High Quality (3 pts) | Medium (2 pts) | Low (1 pt) |
|-----------|---------------------|----------------|------------|
| **Authority** | Academic/government/established publication | Industry publication/news | Blog/social media/unknown |
| **Recency** | Within 1 year | 1-3 years | 3+ years |
| **Evidence** | Data/studies cited | Some citations | No citations/opinion |
| **Objectivity** | Balanced perspective | Some bias clear | Clearly biased |
| **Corroboration** | Confirmed by other sources | Partially confirmed | Single source |

**Minimum Quality Threshold**: 
- Surface research: Average score ≥ 1.5
- Standard research: Average score ≥ 2.0
- Deep research: Average score ≥ 2.5

### 2.3 Domain Authority Quick Reference

| Domain Type | Authority Level | Examples |
|-------------|-----------------|----------|
| **Tier 1 - Highest** | Academic journals, Government databases | Nature, IEEE, .gov sites, PubMed |
| **Tier 2 - High** | Established news, Industry reports | Reuters, Bloomberg, Gartner, McKinsey |
| **Tier 3 - Medium** | Trade publications, Company blogs | TechCrunch, Medium (verified authors) |
| **Tier 4 - Low** | Personal blogs, Social media | Personal websites, Reddit, Twitter |

---

## Phase 3: Verification & Cross-Referencing

### 3.1 Multi-Source Verification

For each key claim, verify using:
- **Primary sources**: Original research, official statements
- **Secondary sources**: News coverage, analysis pieces
- **Tertiary sources**: Encyclopedias, summaries

**Verification Matrix:**

| Claim Type | Minimum Sources | Recommended |
|------------|-----------------|-------------|
| Statistical data | 2 independent | 3+ |
| Expert opinion | 1 expert + 1 context | 2 experts |
| News event | 2 news sources | 3+ from different outlets |
| Scientific finding | 1 peer-reviewed | 2+ peer-reviewed |

### 3.2 Contradiction Resolution Protocol

When sources contradict:

1. **Document the contradiction**: Note exact claims and sources
2. **Assess source quality**: Apply CREDIBILITY Framework to both
3. **Check temporal context**: Is one source outdated?
4. **Identify methodology differences**: Different measurement approaches?
5. **Seek tie-breaker source**: Find third authoritative source
6. **Report transparently**: Present both views with quality assessment

**Example:**
```
⚠️ CONTRADICTION FOUND:
- Source A (Reuters, 2025): "AI adoption increased 40%"
- Source B (McKinsey, 2024): "AI adoption increased 25%"
- Resolution: Different definitions of "adoption" (implementation vs. pilot programs)
- Presenting: Both figures with context
```

### 3.3 Bias Detection

Check for:
- **Selection bias**: Does the source cherry-pick data?
- **Funding bias**: Who sponsored the research?
- **Temporal bias**: Is the timeframe representative?
- **Geographic bias**: Is coverage regionally limited?

**Mitigation**: Actively seek sources from different:
- Geographic regions
- Industry perspectives
- Methodological approaches
- Publication types

---

## Phase 4: Synthesis & Report Generation

### 4.1 Structured Report Template

```markdown
# [Research Topic]

## Executive Summary
[2-3 sentence overview of key findings]

## Key Findings

### Finding 1: [Title]
- **Summary**: [1-2 sentences]
- **Evidence**: [Data points with citations]
- **Confidence**: High/Medium/Low
- **Sources**: [Number of verifying sources]

### Finding 2: [Title]
[Repeat structure]

## Data Comparison Table
| Metric | Source A | Source B | Source C | Notes |
|--------|----------|----------|----------|-------|
| [Data point] | [Value] | [Value] | [Value] | [Context] |

## Contradictions & Limitations
[Document any conflicting information or research gaps]

## Methodology Notes
- Research depth: [Surface/Standard/Deep]
- Sources consulted: [Number]
- Date of research: [Date]
- Queries used: [List]

## Sources
### Primary Sources
1. [Full citation with URL]
2. [...]

### Secondary Sources
1. [Full citation with URL]

### Source Quality Summary
| Source | Authority | Recency | Evidence | Objectivity | Score |
|--------|-----------|---------|----------|-------------|-------|
| [Name] | [Rating] | [Rating] | [Rating] | [Rating] | [Total] |
```

### 4.2 Confidence Indicators

Label each finding with confidence level:

| Level | Criteria | Label |
|-------|----------|-------|
| **High** | 3+ high-quality sources agree | ✅ Confirmed |
| **Medium** | 2 sources agree OR mixed quality | ⚡ Likely |
| **Low** | Single source OR contradictions | ⚠️ Uncertain |

### 4.3 Missing Information Protocol

If information could not be found:
1. **Explicitly state**: "No information found regarding [topic]"
2. **Note search attempts**: List queries that failed
3. **Suggest alternatives**: "Consider searching [related terms]"
4. **Mark as gap**: Include in Limitations section

---

## Phase 5: Quality Assurance

### 5.1 Self-Review Checklist

Before finalizing, verify:
- [ ] All claims have at least one citation
- [ ] Statistics include source and date
- [ ] Contradictions are documented and explained
- [ ] Source quality scores are recorded
- [ ] Missing information is explicitly noted
- [ ] Report follows template structure
- [ ] Executive summary captures key findings

### 5.2 Refinement Loop

If quality thresholds not met:
1. Identify weak areas (low confidence findings)
2. Generate additional targeted queries
3. Seek higher-quality sources
4. Re-verify with new information
5. Update confidence levels

---

## Error Handling & Fallbacks

### No Results Found
1. **Broaden query**: Remove specific terms, add synonyms
2. **Try alternative phrasings**: Different vocabulary
3. **Check spelling/terminology**: Verify correct terms
4. **Report transparently**: Document search attempts

### Source Unavailable
1. **Try archive**: Search for cached versions
2. **Find alternative**: Search for same information elsewhere
3. **Note limitation**: Mark as "source unavailable at time of research"

### Conflicting Information (Unresolvable)
1. **Present both views**: Include all credible perspectives
2. **Assess quality**: Note source quality differences
3. **Mark as contested**: Use "⚠️ Contested claim" label
4. **Recommend further research**: Suggest what additional sources might resolve

---

## Tools Reference

| Tool | Primary Use | When to Use |
|------|-------------|-------------|
| `web_search` | Find URLs | Phase 1-2: Query execution |
| `read_url` | Extract content | Phase 2: Source reading |
| `write_file` | Save findings | Phase 2-4: Intermediate storage |
| `read_file` | Review saved data | Phase 3-4: Synthesis |
| `grep` | Find specific data | Phase 2-3: Data extraction |

---

## Quick Reference Card

```
RESEARCH WORKFLOW:
1. PLAN → Analyze query, decompose into 3-8 targeted sub-queries
2. SEARCH → Execute systematically, track results per query
3. GATHER → Read URLs, assess source quality (CREDIBILITY framework)
4. VERIFY → Cross-reference, resolve contradictions, detect bias
5. SYNTHESIZE → Generate structured report with confidence indicators
6. REVIEW → Self-check, refine if needed

SOURCE MINIMUMS:
- Surface: 3-5 sources, 2-3 queries
- Standard: 5-10 sources, 3-5 queries, 2+ verification
- Deep: 10-20 sources, 5-8 queries, 3+ verification

QUALITY THRESHOLDS:
- Surface: CREDIBILITY score ≥ 1.5
- Standard: CREDIBILITY score ≥ 2.0
- Deep: CREDIBILITY score ≥ 2.5
```