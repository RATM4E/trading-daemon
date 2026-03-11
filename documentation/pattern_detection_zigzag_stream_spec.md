# Pattern Detection from ZigZag Stream

## Purpose

Define deterministic rules for detecting geometric price patterns on a streaming price series using **ZigZag pivots** as the structural input.

Supported pattern classes:

- Converging triangles
- Ascending triangles
- Descending triangles
- Broadening triangles
- Channels (horizontal, ascending, descending)
- Pennants (bullish / bearish)

This specification is intended for rule-based realtime detection and replayable backtesting.

---

# 1. Data Input

The detector consumes:

```
bar = {
  time,
  open,
  high,
  low,
  close
}
```

and a ZigZag engine that produces confirmed pivots:

```
pivot = {
  index,
  time,
  price,
  type   // HIGH or LOW
}
```

The pattern detector works only with confirmed ZigZag pivots.

---

# 2. ZigZag Pivot Engine

## 2.1 Core Requirement

ZigZag must output an alternating pivot sequence:

```
HIGH -> LOW -> HIGH -> LOW
```

or

```
LOW -> HIGH -> LOW -> HIGH
```

The detector must never consume unconfirmed provisional pivots.

---

## 2.2 ZigZag Parameters

Any ZigZag implementation is acceptable if it provides deterministic confirmed pivots.

Typical parameters:

```
depth

deviation

backstep
```

The exact implementation must be fixed and versioned before backtesting.

---

## 2.3 Confirmation Rule

A pivot becomes available only after ZigZag confirms it.

This implies:

- pivot detection is delayed
- backtest and live logic remain consistent
- no use of future information beyond ZigZag confirmation rules

---

# 3. Pivot Sequence Validation

Before pattern detection, pivot stream must pass validation.

## 3.1 Alternation

Required:

```
HIGH LOW HIGH LOW ...
```

If same-side pivot appears due to implementation specifics, keep only the stronger one:

```
for HIGH: keep higher price
for LOW : keep lower price
```

---

## 3.2 Minimum Pattern Size

Pattern candidate requires at least:

```
4 pivots
```

Preferred:

```
5 or more pivots
```

Typical search range:

```
4 .. 8 latest pivots
```

---

# 4. Structural Model

Each pattern is represented by two boundaries:

```
upper_line
lower_line
```

Built from structural anchors.

```
upper_line = line(upper_anchor, upper_last)
lower_line = line(lower_anchor, lower_last)
```

Where:

```
upper_anchor = first structural HIGH of the pattern
upper_last   = latest valid HIGH in the pattern

lower_anchor = first structural LOW of the pattern
lower_last   = latest valid LOW in the pattern
```

---

# 5. Tolerance

A pivot may slightly deviate from the boundary.

Recommended tolerance:

```
tol = max(
  ATR(14) * 0.15,
  pattern_height * 0.03
)
```

A pivot is considered touching a boundary if:

```
distance(pivot, boundary) <= tol
```

---

# 6. Width and Slopes

Pattern width at pivot index `x`:

```
width(x) = upper_line(x) - lower_line(x)
```

Main measures:

```
width_start = width(first_pattern_index)
width_end   = width(last_pattern_index)

su = normalized_slope(upper_line)
sl = normalized_slope(lower_line)
```

Normalized slope:

```
slope = (price2 - price1) / (index2 - index1)
normalized_slope = slope / ATR(14)
```

---

# 7. Pattern States

Recommended state machine:

```
IDLE
FORMING
ACTIVE
BREAKOUT
INVALID
```

Transitions:

```
IDLE -> FORMING   : enough pivots to build candidate
FORMING -> ACTIVE : touch and geometry rules satisfied
ACTIVE -> BREAKOUT: price exits the structure
ACTIVE -> INVALID : structural rule broken
BREAKOUT -> IDLE  : pattern finished
INVALID -> IDLE   : pattern discarded
```

---

# 8. Converging Triangle

Conditions:

```
width_end < width_start
su < 0
sl > 0
```

Minimum touches:

```
upper_touches >= 2
lower_touches >= 2
```

Breakout:

```
close > upper_line + tol
or
close < lower_line - tol
```

---

# 9. Ascending Triangle

Upper boundary is horizontal and fixed.

Conditions:

```
abs(su) < slope_tol
sl > 0
width_end < width_start
```

Define:

```
upper_level = max(high pivots inside pattern)
```

Rules:

- upper boundary is not redefined upward
- only lower boundary may redefine

Breakout:

```
close > upper_level + tol
```

---

# 10. Descending Triangle

Lower boundary is horizontal and fixed.

Conditions:

```
abs(sl) < slope_tol
su < 0
width_end < width_start
```

Define:

```
lower_level = min(low pivots inside pattern)
```

Rules:

- lower boundary is not redefined downward
- only upper boundary may redefine

Breakout:

```
close < lower_level - tol
```

---

# 11. Broadening Triangle

Conditions:

```
width_end > width_start
su > 0
sl < 0
```

Expansion condition:

```
latest_high > previous_high
latest_low  < previous_low
```

Breakout:

```
close > upper_line + tol
or
close < lower_line - tol
```

---

# 12. Horizontal Channel

Conditions:

```
abs(su) < slope_tol
abs(sl) < slope_tol
abs(width_end - width_start) <= width_tol
```

Touches:

```
upper_touches >= 2
lower_touches >= 2
```

Breakout when price leaves the channel.

---

# 13. Ascending Channel

Conditions:

```
su > 0
sl > 0
abs(su - sl) < slope_tol
abs(width_end - width_start) <= width_tol
```

Structure example:

```
L1 -> H1 -> L2 -> H2 -> L3
L2 > L1
H2 > H1
```

---

# 14. Descending Channel

Conditions:

```
su < 0
sl < 0
abs(su - sl) < slope_tol
abs(width_end - width_start) <= width_tol
```

---

# 15. Pennants

Pennant = impulse + small converging triangle.

## 15.1 Impulse Requirement

Before the first pivot of the pennant there must be a directional impulse.

Recommended:

```
impulse_move >= ATR(14) * impulse_factor
```

Typical:

```
impulse_factor = 3 .. 5
```

## 15.2 Size Constraint

Pennant must be small relative to impulse.

```
pattern_height <= impulse_height * 0.5
pattern_bars   <= impulse_bars
```

## 15.3 Bullish Pennant

```
impulse_direction = UP
triangle_detected = TRUE
```

## 15.4 Bearish Pennant

```
impulse_direction = DOWN
triangle_detected = TRUE
```

---

# 16. Boundary Redefinition Logic

A key rule of this detector is that a pattern is a **living structure**, not a fixed set of 5 pivots.

A new pivot may:

- confirm a boundary
- redefine a boundary
- invalidate the pattern

---

## 16.1 Upper Boundary Redefinition

If a new HIGH pivot appears:

### Case A. Confirmation

```
distance(new_high, upper_line) <= tol
```

Then it is another upper touch.

### Case B. Redefinition

If:

```
new_high > previous_internal_high
and
new_high <= upper_anchor + tol
```

Then redefine:

```
upper_line = line(upper_anchor, new_high)
upper_last = new_high
```

This is valid only if after redefinition:

```
pattern geometry still matches class
apex remains ahead for converging triangles
internal pivots still fit the envelope within tol
```

### Case C. Invalidation

If:

```
new_high > upper_anchor + tol
```

Then the structure is broken from above.

---

## 16.2 Lower Boundary Redefinition

If a new LOW pivot appears:

### Case A. Confirmation

```
distance(new_low, lower_line) <= tol
```

### Case B. Redefinition

If:

```
new_low < previous_internal_low
and
new_low >= lower_anchor - tol
```

Then:

```
lower_line = line(lower_anchor, new_low)
lower_last = new_low
```

This is valid only if pattern geometry is preserved.

### Case C. Invalidation

If:

```
new_low < lower_anchor - tol
```

Then the structure is broken from below.

---

# 17. Special Rule for Flat-Side Triangles

## Ascending Triangle

- upper side is fixed
- redefine allowed only on lower side
- upward update of flat top is not allowed

## Descending Triangle

- lower side is fixed
- redefine allowed only on upper side
- downward update of flat bottom is not allowed

---

# 18. Broadening Triangle Update Rule

For broadening triangles both boundaries may redefine.

Valid expansion requires:

```
new_high > previous_high + tol
and/or
new_low < previous_low - tol
```

But after update:

```
width_end must remain greater than width_start
internal pivots must still fit broadening geometry
```

---

# 19. Apex Calculation

For converging triangles:

```
upper_line: y = a1*x + b1
lower_line: y = a2*x + b2
x_apex = (b2 - b1) / (a1 - a2)
```

Triangle valid only if:

```
x_apex > last_pivot_index
```

Optional maturity constraint:

```
x_apex <= last_pivot_index + max_future_bars
```

Typical:

```
max_future_bars = pattern_length * 2
```

---

# 20. Minimum Touch Rules

Minimum:

```
upper_touches >= 2
lower_touches >= 2
```

Preferred:

```
total_pivots >= 5
```

Higher confidence:

```
total_pivots >= 6
```

---

# 21. Pattern Lifetime

Patterns should expire if too old.

```
pattern_pivots <= max_pattern_pivots
```

Typical:

```
max_pattern_pivots = 8
```

Optional bar-based limit:

```
pattern_bars <= 200
```

---

# 22. Channel False Positive Filter

Reject channel if:

```
abs(width_end - width_start) > width_start * 0.25
```

or

```
max_width / min_width > 1.4
```

Also reject if pivot fit error is too large.

---

# 23. Breakout Detection

Breakout occurs when closing price exits the active structure.

```
close > upper_line + tol
or
close < lower_line - tol
```

Optional strength filter:

```
breakout_range >= ATR(14) * breakout_factor
```

Typical:

```
breakout_factor = 0.8
```

---

# 24. Pattern Quality Score

To rank candidates and suppress weak patterns use:

```
quality_score in [0 .. 100]
```

Composite form:

```
quality_score =
    w1 * touch_score +
    w2 * fit_score +
    w3 * geometry_score +
    w4 * symmetry_score +
    w5 * breakout_potential_score
```

Weights are configurable and sum to 1.

---

## 24.1 Touch Score

```
touch_score = min(total_touches / required_touches, 1.0)
```

Typical:

```
required_touches = 4
```

## 24.2 Fit Score

```
mean_dist = mean(distance_of_touch_pivots_to_boundaries)
fit_score = max(0, 1 - mean_dist / tol)
```

## 24.3 Geometry Score

Triangles:

```
geometry_score = clamp((width_start - width_end) / width_start, 0, 1)
```

Broadening:

```
geometry_score = clamp((width_end - width_start) / width_start, 0, 1)
```

Channels:

```
geometry_score = max(0, 1 - abs(width_end - width_start) / width_start)
```

## 24.4 Symmetry Score

```
symmetry_score = 1 - abs(upper_touches - lower_touches) / total_touches
```

## 24.5 Breakout Potential Score

For converging structures:

```
apex_progress = (current_index - start_index) / (x_apex - start_index)
```

Preferred maturity zone:

```
0.5 <= apex_progress <= 0.85
```

## 24.6 Threshold

Pattern is actionable only if:

```
quality_score >= quality_threshold
```

Typical:

```
quality_threshold = 60
```

Strict mode:

```
quality_threshold = 70 .. 80
```

---

# 25. Pattern Priority Rules

When the same pivots satisfy multiple geometries, apply priority:

```
Pennant
Triangle
Channel
Broadening
```

Reason:

- pennants require context and are most specific
- triangles represent compression
- channels are broader equilibrium structures
- broadening patterns are least stable

---

# 26. Stream Processing Loop

Pseudo-algorithm:

```
for each new confirmed ZigZag pivot:

  1. append pivot to pivot sequence
  2. validate alternation
  3. update current candidate pattern
  4. check redefine / invalidation rules
  5. recompute geometry
  6. recompute quality_score
  7. if breakout -> emit breakout event
  8. if invalid  -> emit invalid event and reset
```

---

# 27. Output Events

Recommended events:

```
PATTERN_START
PATTERN_UPDATE
PATTERN_REDEFINED_UPPER
PATTERN_REDEFINED_LOWER
PATTERN_BREAKOUT_UP
PATTERN_BREAKOUT_DOWN
PATTERN_INVALID
```

Each event contains:

```
pattern_type
start_index
end_index
pivot_indices
upper_line_params
lower_line_params
pattern_height
pattern_width
slope_upper
slope_lower
quality_score
breakout_direction
```

---

# 28. Recommended Default Parameters

```
slope_tol            = 0.05 normalized units
width_tol            = 0.10 * width_start
quality_threshold    = 60
max_pattern_pivots   = 8
impulse_factor       = 4
breakout_factor      = 0.8
```

ZigZag parameters must be explicitly fixed in the implementation.

---

# 29. Practical Use with OHLC Detector

Running this ZigZag-based detector in parallel with an OHLC-based pivot detector is reasonable.

Recommended comparison dimensions:

- pattern frequency
- stability of boundary geometry
- breakout timing
- false breakout rate
- average detection delay
- replay consistency

General expectation:

- ZigZag detector gives cleaner structures
- OHLC pivot detector reacts earlier
- combined use helps evaluate robustness

---

# 30. Notes

This detector is intended for:

- deterministic streaming detection
- consistent backtesting
- direct implementation in Python, MQL5, C#, C++ or Pine

No machine learning required.

