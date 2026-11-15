---
name: blog-post-writer
description: Write high-quality, engaging blog posts that are SEO-optimized and formatted for maximum readability. Use this skill whenever the user wants to create blog content, articles, or posts - whether they're creating new content from scratch, improving existing drafts, asking for help with blog writing, or needing assistance with any aspect of blog post creation including headlines, structure, SEO optimization, or formatting. This skill covers all blog post types including how-to guides, listicles, opinion pieces, tutorials, ultimate guides, and more. Don't wait for the user to explicitly ask for "blog post writing help" - if they mention writing, creating, or publishing blog content, activate this skill immediately.
---

# Blog Post Writer

A skill for creating well-structured, engaging, SEO-friendly blog posts that capture attention and drive results.

## When to Use This Skill

Activate this skill when the user:
- Wants to write a new blog post or article
- Needs help structuring their blog content
- Asks for SEO optimization of blog posts
- Needs headline or title suggestions
- Wants to improve an existing blog draft
- Is creating any type of blog content (how-to, listicle, opinion, tutorial, guide, etc.)
- Needs formatting guidelines for blog posts

## What This Skill Does

This skill generates complete, publication-ready blog posts in Markdown format with:
- Compelling headlines that grab attention
- Logical structure with proper heading hierarchy (H1, H2, H3)
- Engaging introduction with a strong hook
- Well-developed body sections with actionable content
- Compelling conclusion with clear call-to-action
- SEO-optimized content with strategic keyword placement
- Scannable formatting (short paragraphs, bullet points, bold text)
- Internal linking opportunities (marked as placeholders)

---

## Required Inputs

Before writing, gather these inputs from the user:

1. **Topic**: What is the blog post about? (e.g., "how to write compelling headlines")
2. **Target Audience**: Who is this post for? (e.g., "beginner content marketers")
3. **Blog Post Type**: What format best suits the content?
   - How-to guide (step-by-step instructions)
   - Listicle (numbered list of items)
   - Opinion piece (personal perspective)
   - Tutorial (detailed learning guide)
   - Ultimate guide (comprehensive resource)
   - Problem-solution (addressing a pain point)
   - Other (specify)
4. **Primary Keyword**: Main SEO keyword to target
5. **Tone**: Professional, casual, authoritative, friendly, humorous?
6. **Word Count Target**: Approximate length (500-1000, 1000-1500, 1500-2500, 2500+)
7. **Any Specific Points to Include**: Key points, examples, or sections the user wants covered
8. **Call-to-Action**: What should readers do after reading? (e.g., subscribe, comment, download)

If any required input is missing, ask the user before proceeding.

---

## Writing Procedure

Follow these steps to create a high-quality blog post:

### Step 1: Research and Plan

Before writing, understand the topic thoroughly. If the user hasn't provided enough detail, ask clarifying questions about:
- The specific angle or perspective to take
- Any competitor or reference articles to be aware of
- Unique insights or personal experience to include
- Technical level expected of the audience

### Step 2: Craft a Compelling Headline

The headline is the most important element - it determines whether readers click. Use these formulas:
- "X Ways to [Desired Outcome]" (listicle)
- "How to [Achieve Result] in X Steps" (how-to)
- "The Ultimate Guide to [Topic]" (comprehensive guide)
- "Why [Common Belief] Is Wrong" (contrarian/opinion)
- "[Number] [Topic] Every [Audience] Should Know"

Include a power word: Ultimate, Proven, Essential, Secret, Simple, Complete, Practical, Effective, Step-by-Step

**Example:**
- Input: topic = "email marketing for small businesses"
- Output: "7 Proven Email Marketing Strategies for Small Business Owners That Actually Work"

### Step 3: Write the Introduction

The introduction must hook readers in the first 15 seconds. Include:
- **Hook**: Start with a surprising statistic, question, or statement
- **Problem**: Acknowledge the reader's pain point
- **Promise**: State what they'll learn or achieve
- **Credibility**: Briefly establish authority

**Example hook:**
"Most blog posts get read for only 15 seconds. Here's how to change that."

### Step 4: Structure the Body

Use heading hierarchy properly:
- H1: Main title (only one per post)
- H2: Major sections (use 3-5 for most posts)
- H3: Sub-points within sections

For each section:
- Start with a clear topic sentence
- Develop with examples, data, or actionable steps
- Keep paragraphs to 2-4 sentences (under 100 words)
- Use bullet points for lists
- Use bold text for key terms and takeaways

### Step 5: Optimize for SEO

Integrate keywords naturally throughout:
- In the headline (preferably near the beginning)
- In the introduction (first 100 words)
- In 2-3 H2 headings
- Naturally throughout body content
- In the conclusion

Avoid keyword stuffing - use synonyms and related terms. Aim for 1-2% keyword density.

### Step 6: Write a Strong Conclusion

Summarize key takeaways in 2-3 sentences. Include:
- Clear call-to-action (comment, share, subscribe, download)
- Encouragement to take the next step
- Optional: invitation to ask questions

### Step 7: Add Formatting Elements

Before finalizing, add:
- **Bold** for key terms and important points
- Bullet or numbered lists for scannable content
- Placeholder for images with [IMAGE: description] tags
- Blockquotes for expert quotes or key statistics
- Internal linking opportunities marked as [LINK: anchor text - relevant internal page]

---

## Output Format

ALWAYS use this exact template structure:

```markdown
# [Compelling Headline with Primary Keyword]

[Engaging introduction paragraph with hook, problem, and promise]

## [H2: First Main Section - addresses key aspect of topic]

[Supporting paragraphs with examples, data, or explanations]

### [H3: Sub-section if needed]

[Additional detail or steps]

## [H2: Second Main Section]

[Content with clear flow to next section]

## [H2: Third Main Section]

[Content that builds on previous sections]

## [H2: Fourth Section if needed]

## Conclusion

[Summary of key takeaways + clear call-to-action]

---

*Tags: [primary keyword], [related keyword 1], [related keyword 2]*

*Category: [appropriate category]*
```

---

## Example Inputs and Outputs

### Example 1: How-To Guide

**Input:**
- Topic: "how to write compelling blog headlines"
- Audience: "content marketers and bloggers"
- Type: How-to guide
- Keyword: "write compelling headlines"
- Tone: Professional but accessible
- Length: 1000-1500 words

**Output:**
```markdown
# How to Write Compelling Blog Headlines That Drive Clicks and Traffic

Your headline is the first (and sometimes only) impression you make on potential readers. Studies show that 80% of readers never make it past the headline, making it the most critical element of any blog post. In this guide, you'll learn proven formulas for writing headlines that capture attention and drive traffic...

## Why Headlines Matter More Than You Think

The average reader decides whether to click on your content in just 15 seconds. Your headline has one job: convince them that spending time reading your post is worth it. A great headline promises value and creates curiosity...

### The Psychology Behind Click-Worthy Headlines

Readers scan headlines looking for three things: relevance, value, and curiosity. Your headline must immediately communicate that your content addresses their problem or desire...

## 5 Proven Headline Formulas That Work

Here are the most effective headline structures used by top content marketers:

1. **The Number + Promise Formula**: "7 Ways to [Desired Outcome]"
2. **The How-To Formula**: "How to [Achieve Result] in [Timeframe]"
3. **The Ultimate Guide Formula**: "The Ultimate Guide to [Topic]"
4. **The Contrarian Formula**: "Why [Common Belief] Is Wrong"
5. **The Secret Formula**: "The [Number] Secrets [Expert] Don't Tell You"

[Each formula explained with examples]

## Conclusion

Writing compelling headlines is a skill that improves with practice. Start with these proven formulas, test different variations, and track your results. Remember: a great headline can double your traffic without changing a single word of your content.

---

*Tags: blog headlines, headline writing, content marketing, copywriting*

*Category: Content Marketing*
```

### Example 2: Listicle

**Input:**
- Topic: "best productivity tools for remote workers"
- Audience: "remote workers and freelancers"
- Type: Listicle
- Keyword: "productivity tools remote workers"
- Tone: Friendly and practical
- Length: 1500-2000 words

**Output:**
```markdown
# 10 Best Productivity Tools for Remote Workers in 2024

Working from home sounds great until you realize your kitchen table has become your office, your couch is your meeting room, and "quickly checking email" has turned into a 2-hour rabbit hole. If you're struggling to stay productive while working remotely, these tools will transform your workflow...

## The Challenge of Remote Work Productivity

Remote work offers freedom, but it also requires serious discipline. Without the structure of a traditional office, it's easy to lose track of time, miss deadlines, or blur the lines between work and personal life. The right productivity tools can help...

## 10 Tools That Will Transform Your Remote Work

### 1. Todoist - Your Brain's External Hard Drive

[Description of tool with use case]

### 2. Notion - The All-in-One Workspace

[Description with practical example]

[Continue for all 10 tools]

## Conclusion

The best productivity tool is the one you'll actually use. Start with one or two from this list, master them, then expand your toolkit as needed. Your remote work career will thank you.

---

*Tags: productivity tools, remote work, work from home, productivity apps*

*Category: Technology*
```

---

## Writing Style Guidelines

### Tone and Voice
- Write as if speaking to a knowledgeable friend - clear but not condescending
- Use active voice ("You should do X" not "X should be done")
- Avoid jargon unless your audience expects it
- Include personal insights or examples when appropriate

### Readability Principles
- Keep sentences under 25 words when possible
- Use 2-4 sentence paragraphs for better scanning
- One idea per paragraph
- Use transition words between paragraphs (however, moreover, finally, etc.)
- White space is your friend - don't fear short sections

### SEO Integration
- Place primary keyword in first 100 words
- Use keywords in 2-3 subheadings
- Include 1-2 related keywords naturally
- Write meta description (150-160 characters) as a separate output
- Use descriptive alt text for any images

### Engagement Techniques
- Open with a hook (question, statistic, surprising statement)
- Use "you" and "your" to speak directly to reader
- Include specific examples and actionable steps
- End with a clear call-to-action
- Invite engagement with questions

---

## Quality Checklist

Before delivering the blog post, verify:

- [ ] Headline includes primary keyword and creates curiosity
- [ ] Introduction has a strong hook and states the post's value
- [ ] H2 headings clearly organize content into logical sections
- [ ] Each section has substantial content (not just a paragraph)
- [ ] Paragraphs are short (2-4 sentences) and scannable
- [ ] Primary keyword appears naturally throughout
- [ ] At least one list (bullet or numbered) for scannability
- [ ] Bold text highlights key terms and important points
- [ ] Conclusion summarizes and includes clear CTA
- [ ] Content matches the specified blog post type
- [ ] Tone is appropriate for the target audience
- [ ] Word count is within the specified range

---

## Meta Description Template

After the blog post, provide a meta description in this format:

```markdown
**Meta Description:**
[150-160 characters including primary keyword - compelling summary that encourages click-through]
```

**Example:**
"Learn how to write headlines that grab attention and double your traffic with these 5 proven formulas. Perfect for content marketers."