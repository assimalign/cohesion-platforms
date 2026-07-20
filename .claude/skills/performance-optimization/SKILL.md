---
name: performance-optimization
description: Analyze code for performance bottlenecks — algorithmic complexity, memory allocation, async/concurrency, and I/O — and report prioritized, quantified optimization recommendations. Use when the user asks for a performance review, profiling guidance, or optimization of a specific file, project, or diff.
---

# Performance Optimization

Analyze the provided code for performance bottlenecks and optimization opportunities. Conduct a thorough review covering:

## Areas to Analyze

### Algorithm Efficiency
- Time complexity issues (O(n²) or worse when better exists)
- Nested loops that could be optimized
- Redundant calculations or repeated work
- Inefficient data structure choices
- Missing memoization or dynamic programming opportunities

### Memory Management
- Memory leaks or retained references
- Loading entire datasets when streaming is possible
- Excessive object instantiation in loops
- Large data structures kept in memory unnecessarily
- Allocations in hot paths (prefer `Span<T>`/`Memory<T>` for buffers, `ValueTask<T>` for sync-fast-path async — see `.claude/rules/general-rules.md` § Performance)

### Async & Concurrency
- Blocking I/O operations that should be async
- Sequential operations that could run in parallel
- Synchronous file operations
- Unoptimized worker thread usage

### Network & I/O
- Excessive API calls (missing request batching)
- No response caching strategy
- Large payloads without compression
- Lack of connection reuse

## Output Format

For each issue identified:
1. **Issue**: Describe the performance problem
2. **Location**: Specify file/function/line numbers
3. **Impact**: Rate severity (Critical/High/Medium/Low) and explain expected performance degradation
4. **Current Complexity**: Include time/space complexity where applicable
5. **Recommendation**: Provide specific optimization strategy
6. **Code Example**: Show optimized version when possible
7. **Expected Improvement**: Quantify performance gains if measurable

If code is well-optimized:
- Confirm optimization status
- List performance best practices properly implemented
- Note any minor improvements possible

**Code to review:**
```
$ARGUMENTS
```
