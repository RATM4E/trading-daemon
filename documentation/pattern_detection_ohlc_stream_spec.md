# Pattern Detection from OHLC Stream

## Purpose

Define deterministic rules for detecting geometric price patterns on a streaming OHLC bar sequence. The detector operates **without indicators or ZigZag**, using only price data.

Supported pattern classes:

- Converging triangles
- Broadening triangles
- Channels (horizontal, ascending, descending)
- Pennants (bullish / bearish)

The detector works incrementally as new bars arrive.

---

# 1. Data Input

Each bar:

```
bar = {
  time,
  open,
  high,
  low,
  close
}
```

Bars arrive sequentially.

The detector maintains rolling state.

---

# 2. Pivot Extraction

Patterns are defined using **pivot points** derived from OHLC.

## 2.1 Fractal Pivot Rule

A pivot HIGH at bar `i` if:

```
high[i] > high[i-k ... i-1]
high[i] >= high[i+1 ... i+k]
```

Pivot LOW if:

```
low[i] < low[i-k ... i-1]
low[i] <= low[i+1 ... i+k]
```

Typical value:

```
k = 2..4
```

---

## 2.2 Minimum Swing Filter

A pivot must move sufficiently away from the previous pivot.

```
swing = |price(pivot_i) - price(pivot_{i-1})|
```

Condition:

```
swing >= ATR(14) * swing_factor
```

Typical:

```
swing_factor = 0.8 .. 1.5
```

---

## 2.3 Alternation Enforcement

Pivot sequence must alternate:

```
HIGH → LOW → HIGH → LOW
```

If two pivots of the same type appear consecutively:

- keep the **stronger** pivot

Rules:

```
if HIGH:
  keep larger high

if LOW:
  keep smaller low
```

---

# 3. Pattern Window

Patterns are evaluated over recent pivots.

Typical window:

```
4 – 8 pivots
```

Minimum requirement:

```
pivot_count >= 4
```

---

# 4. Boundary Construction

Two lines define a candidate pattern.

```
upper_line
lower_line
```

Lines are computed from pivot anchors.

```
upper_line = line(upper_anchor, upper_last)
lower_line = line(lower_anchor, lower_last)
```

Where:

```
upper_anchor = first significant HIGH
upper_last   = most recent HIGH

lower_anchor = first significant LOW
lower_last   = most recent LOW
```

---

# 5. Distance Tolerance

Pivot points may deviate from boundaries.

Tolerance:

```
tol = max(
  ATR(14) * 0.15,
  pattern_height * 0.03
)
```

Pivot considered touching boundary if:

```
distance(pivot, boundary) <= tol
```

---

# 6. Width Definition

Pattern width at bar `x`:

```
width(x) = upper_line(x) - lower_line(x)
```


```
width_start = width(first_pivot_index)
width_end   = width(last_pivot_index)
```

---

# 7. Pattern Types

Patterns classified using slopes and width behavior.

Let:

```
su = slope(upper_line)
sl = slope(lower_line)
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
close < lower_line - tol
```

---

# 9. Ascending Triangle

Upper boundary horizontal.

Conditions:

```
|su| < slope_tol
sl > 0
width_end < width_start
```

Upper boundary is **fixed level**.

```
upper_level = max(high pivots)
```

Only lower boundary may redefine.

Breakout:

```
close > upper_level + tol
```

---

# 10. Descending Triangle

Lower boundary horizontal.

Conditions:

```
|sl| < slope_tol
su < 0
width_end < width_start
```

Lower boundary fixed.

```
lower_level = min(low pivots)
```

Breakout:

```
close < lower_level - tol
```

---

# 11. Broadening Triangle

Range expands over time.

Conditions:

```
width_end > width_start
su > 0
sl < 0
```

Extremes expand:

```
H2 > H1
L2 < L1
```

Breakout:

```
close > upper_line + tol
close < lower_line - tol
```

---

# 12. Horizontal Channel

Conditions:

```
|su| < slope_tol
|sl| < slope_tol

|width_end - width_start| < width_tol
```

Touches:

```
upper >= 2
lower >= 2
```

Breakout when price leaves channel.

---

# 13. Ascending Channel

Conditions:

```
su > 0
sl > 0

|su - sl| < slope_tol

width_end ≈ width_start
```

Sequence example:

```
L1 → H1 → L2 → H2 → L3

L2 > L1
H2 > H1
```

---

# 14. Descending Channel

Conditions:

```
su < 0
sl < 0

|su - sl| < slope_tol

width_end ≈ width_start
```

---

# 15. Pennants

Pennant = impulse + small converging triangle.

## Impulse detection

```
impulse_move >= ATR(14) * impulse_factor
```

Typical:

```
impulse_factor = 3..5
```

---

## Pattern size constraint

```
pattern_height <= impulse_height * 0.5
pattern_bars   <= impulse_bars
```

---

## Bullish Pennant

```
impulse_direction = UP
triangle_detected
```

Breakout expected upward.

---

## Bearish Pennant

```
impulse_direction = DOWN
triangle_detected
```

Breakout expected downward.

---

# 16. Boundary Redefinition

When new pivot appears.

## Upper pivot

If:

```
pivot_high > last_high
pivot_high <= upper_anchor + tol
```

Then redefine:

```
upper_line = line(upper_anchor, pivot_high)
```

---

## Lower pivot

If:

```
pivot_low < last_low
pivot_low >= lower_anchor - tol
```

Then redefine:

```
lower_line = line(lower_anchor, pivot_low)
```

---

# 17. Pattern Invalidity

Pattern becomes invalid if:

```
pivot breaks structural anchor
```

Examples:

```
pivot_high > upper_anchor + tol
pivot_low  < lower_anchor - tol
```

or geometry no longer satisfies pattern rules.

---

# 18. Breakout Detection

Breakout occurs when closing price exits pattern.

```
close > upper_line + tol
```

or

```
close < lower_line - tol
```

Optional confirmation:

```
breakout_bar_range > ATR
```

---

# 19. Stream Processing Loop

Pseudo‑algorithm:

```
for each new bar:

  update pivot detector

  if new pivot detected:

      update active pattern

      if pattern invalid:
          reset pattern

      if breakout:
          emit signal
```

---

# 20. Recommended Default Parameters

```
fractal_k        = 3
swing_factor     = 1.0
slope_tol        = small value (~0.0001 normalized)
width_tol        = 0.1 * width_start
impulse_factor   = 4
pattern_max_pivots = 8
```

---

# 21. Output Structure

Detector emits events:

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
pivot_indices
upper_line
lower_line
width
slope_upper
slope_lower
```

---

# 22. Notes

The detector is designed for:

- streaming market data
- deterministic rule-based detection
- reproducibility across platforms

No machine learning or statistical fitting required.

---

# 23. Slope Definition

Slope must be normalized to avoid dependence on price scale.

Recommended definition:

```
slope = (price2 - price1) / (index2 - index1)
```

Normalized slope:

```
normalized_slope = slope / ATR(14)
```

Typical tolerances:

```
slope_tol = 0.05
```

---

# 24. Apex Calculation (Triangles)

Apex is the intersection point of the two boundaries.

Given:

```
upper_line: y = a1*x + b1
lower_line: y = a2*x + b2
```

Intersection index:

```
x_apex = (b2 - b1) / (a1 - a2)
```

Triangle valid only if:

```
x_apex > last_pivot_index
```

Optional constraint:

```
x_apex <= last_pivot_index + max_future_bars
```

Typical:

```
max_future_bars = pattern_length * 2
```

---

# 25. Minimum Touch Rules

To reduce false patterns:

```
upper_touches >= 2
lower_touches >= 2
```

Preferred:

```
total_pivots >= 5
```

Touches counted if pivot distance from boundary <= tolerance.

---

# 26. Channel False Positive Filter

Channels should maintain stable width.

Reject candidate channel if:

```
|width_end - width_start| > width_start * 0.25
```

or

```
max_width / min_width > 1.4
```

Also reject if pivot fit error too large.

---

# 27. Pivot Detection Without Lookahead

Streaming pivot detection must avoid future bars.

Instead of symmetric fractals, use delayed confirmation.

A HIGH pivot at bar i becomes confirmed only after k bars:

```
if high[i] > high[i-k ... i-1]
and high[i] >= high[i+1 ... i+k]
```

Confirmation delay:

```
pivot_confirm_delay = k bars
```

Typical:

```
k = 3
```

---

# 28. Pattern Priority Rules

Multiple pattern geometries may fit the same pivots.

Priority order recommended:

```
Pennant
Triangle
Channel
Broadening
```

Reason:

- pennants depend on context (impulse)
- triangles represent compression
- channels represent equilibrium
- broadening patterns are rare and less stable

---

# 29. Pattern Lifetime

Patterns should expire if they persist too long.

```
pattern_bars <= max_pattern_bars
```

Typical:

```
max_pattern_bars = 200
```

or

```
pattern_length <= 3 * pivot_spacing
```

---

# 30. Breakout Strength Filter (Optional)

To avoid false breakouts:

```
breakout_range >= ATR(14) * breakout_factor
```

Typical:

```
breakout_factor = 0.8
```

Volume confirmation may also be added if volume data available.

---

# 31. Detector State Machine

Recommended states:

```
IDLE
FORMING
ACTIVE
BREAKOUT
INVALID
```

Transitions:

```
IDLE -> FORMING (first valid pivots)
FORMING -> ACTIVE (touch rules satisfied)
ACTIVE -> BREAKOUT (price exits boundary)
ACTIVE -> INVALID (structure broken)
BREAKOUT -> IDLE (pattern finished)
```

---

# 32. Event Payload

Each emitted event should contain:

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
breakout_direction
```

This allows deterministic replay and backtesting.

---

# 33. Pattern Quality Score

A pattern quality score helps rank candidates and suppress weak structures.

Recommended score range:

```
0 .. 100
```

Composite score:

```
quality_score =
    w1 * touch_score +
    w2 * fit_score +
    w3 * geometry_score +
    w4 * symmetry_score +
    w5 * breakout_potential_score
```

Where weights are configurable and sum to 1.

---

## 33.1 Touch Score

Measures whether both boundaries are sufficiently confirmed.

Example:

```
touch_score = min(total_touches / required_touches, 1.0)
```

Recommended:

```
required_touches = 4
```

---

## 33.2 Fit Score

Measures how closely pivots align with boundaries.

Let:

```
mean_dist = mean(boundary_distance_of_all_touch_pivots)
```

Then:

```
fit_score = max(0, 1 - mean_dist / tol)
```

Higher is better.

---

## 33.3 Geometry Score

Measures how well the shape matches its class.

Examples:

For triangles:

```
geometry_score = clamp((width_start - width_end) / width_start, 0, 1)
```

For broadening patterns:

```
geometry_score = clamp((width_end - width_start) / width_start, 0, 1)
```

For channels:

```
geometry_score = max(0, 1 - abs(width_end - width_start) / width_start)
```

---

## 33.4 Symmetry Score

Measures balance of pivot spacing and structure.

Examples:

- similar number of touches on both boundaries
- similar average spacing between pivots
- no large internal voids

Simple version:

```
symmetry_score = 1 - abs(upper_touches - lower_touches) / total_touches
```

---

## 33.5 Breakout Potential Score

Measures whether pattern is mature enough for a breakout.

Examples:

- current bar close is near one of the boundaries
- pattern has consumed 50% to 85% of distance to apex
- width has sufficiently compressed

Example:

```
apex_progress = (current_index - start_index) / (x_apex - start_index)
```

Valid maturity zone:

```
0.5 <= apex_progress <= 0.85
```

---

## 33.6 Minimum Quality Threshold

Pattern becomes tradable / valid only if:

```
quality_score >= quality_threshold
```

Typical:

```
quality_threshold = 60
```

For stricter filtering:

```
quality_threshold = 70 .. 80
```

---

# 34. Pivot Clustering and Merge Rules

Micro-pivots often create false patterns. Nearby pivots should be merged.

The goal is to preserve structure while removing noise.

---

## 34.1 Same-Side Consecutive Pivot Merge

If two consecutive pivots have the same type:

```
HIGH, HIGH
or
LOW, LOW
```

Keep only the stronger one.

Rules:

```
for HIGH: keep larger price
for LOW : keep smaller price
```

---

## 34.2 Time Proximity Merge

If two pivots of the same type occur too close in time, merge them.

Condition:

```
(index2 - index1) < min_pivot_spacing
```

Typical:

```
min_pivot_spacing = 3 .. 10 bars
```

If merged:

```
HIGH -> keep higher high
LOW  -> keep lower low
```

---

## 34.3 Price Proximity Merge

If two same-side pivots are very close in price, treat them as one structural point.

Condition:

```
abs(price2 - price1) <= ATR(14) * merge_price_factor
```

Typical:

```
merge_price_factor = 0.2 .. 0.5
```

---

## 34.4 Internal Noise Pivot Removal

A pivot may be discarded if it does not materially change geometry.

Example rule:

A pivot is removable if all conditions hold:

```
1. it lies between two stronger neighboring pivots
2. removing it does not change pattern class
3. removing it changes boundary slope by less than slope_noise_tol
```

Typical:

```
slope_noise_tol = 0.02 normalized units
```

---

## 34.5 Compression of Pivot Chain

After extraction, run a cleanup pass:

```
1. enforce alternation
2. merge same-side pivots
3. merge near-time pivots
4. merge near-price pivots
5. remove internal noise pivots
```

Result:

```
clean_pivot_sequence
```

This sequence is the only one used for pattern detection.

---

# 35. Recommended Detection Pipeline

Final stable pipeline:

```
for each new bar:

  1. update ATR
  2. check pivot confirmation
  3. append raw pivot if confirmed
  4. run pivot cleanup / merge
  5. update active candidate pattern
  6. score pattern quality
  7. emit pattern state/event if valid
  8. detect breakout or invalidation
```

---

# 36. Practical Notes on Stability

The two strongest anti-noise mechanisms are:

1. pivot cleanup / merge
2. minimum quality threshold

Without them the detector will overproduce weak triangles and pseudo-channels.

Recommended default behavior:

- detect generously
- validate strictly
- trigger only on high-quality mature structures

