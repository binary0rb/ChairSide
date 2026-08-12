# ChairSide Canonical Reporting Design

## Status and authority

This document is the canonical design record for ChairSide reporting semantics approved under issue #212 and the reporting redesign tracked by #211.

It defines the intended reporting meaning that later backend, UI, and test work must implement. It does not change production behavior by itself.

Lifecycle truth remains owned by the canonical room lifecycle and accepted Ready-handoff design:

- `docs/design/prestage-assignment-lifecycle.md` defines room lifecycle and Ready handoff behavior.
- Issue #111 defines accepted reporting handoff and legacy compatibility rules.
- This document consumes those facts for reporting and must not redefine them inconsistently.

When current reporting code differs from this document, treat the difference as an implementation gap owned by the applicable child issue under #211. Do not silently change lifecycle or persistence semantics to make reporting easier.

## Governing principles

- ChairSide reports only activity it can observe from its room lifecycle data.
- Reporting must not infer attendance, productivity, availability, scheduled hours, or unobserved activity.
- Ready for Doctor is the formal room-to-doctor handoff boundary for canonical cases.
- Prominent operational timing is median-first. Averages may remain available as secondary detail.
- No qualifying ChairSide activity for a doctor on a calendar day means no doctor-day observation. The day is omitted and never represented as zero.
- Wall-clock overlap calculations use interval unions or equivalent sweep-line accounting so simultaneous intervals are never double-counted as elapsed time.
- Procedure reporting describes observed case mix and scheduling characteristics, not procedure or provider performance.
- Schedule Fit evaluates scheduling assumptions, not the doctor.
- Calibration Insights identify sufficiently supported scheduling patterns for human review and never change expected allocation automatically.
- Every narrowed analytical population retains visible sample-size context.
- Healthy Data Quality stays quiet. Exceptions and exclusions become prominent only when context or action is needed.
- Audit detail remains available as the evidence layer behind summary metrics and insights.
- No provider ranking, efficiency score, attendance inference, idle-time report, leaderboard, grade, quota, or punitive staff metric is introduced.

## Approved information hierarchy

### Practice Overview

The primary Practice Overview cards are:

- Completed Cases;
- Median Ready Wait;
- Median Seated -> Doctor;
- Median Turnover.

Ready Wait is the primary wait metric. Seated -> Doctor remains the truthful total patient-seated interval and stays visible as a distinct flow metric.

### Practice Trends

Primary practice trends are:

- Median Ready Wait;
- Median Turnover.

Seated -> Doctor remains available as a secondary flow trend. Comparison language must remain conservative and sample-aware.

### Procedure Mix

Procedure Mix exists at both Practice and Doctor scope with counts, percentages, and visible sample-size context.

### Doctor Reporting

Doctor reporting is organized around observed clinical flow rather than allocation balance. The approved overview concepts are:

- Completed Cases;
- Median Ready Wait;
- Median Doctor Time;
- Observed Clinical Span;
- Peak Concurrent Rooms;
- Observed Doctor Days.

Allocation analysis belongs to Schedule Fit rather than defining Doctor Overview.

### Procedure Intelligence

Procedure Intelligence may show:

- case count;
- median Doctor Time;
- typical observed range;
- expected allocation as scheduling context;
- doctor x procedure drill-down;
- sedation context.

### Schedule Fit and Calibration Insights

Schedule Fit reports compatible expected-vs-observed case-flow timing, slack, debt, signed net variance, and population coverage. Calibration Insights may surface sufficiently supported patterns without changing scheduling assumptions automatically.

### Data Quality and audit

Healthy Data Quality remains quiet. Exclusions, limited samples, and pending review use progressive disclosure. Case audit remains the evidence layer behind summary metrics and insights.

## Reporting time and attribution foundations

### Report windows

Existing reporting-window semantics remain unchanged unless a later approved issue explicitly changes them:

- date filters use whole UTC calendar days;
- completed-cycle windows remain anchored on `DoctorCompleteAt`;
- weekly trend buckets use Monday-start UTC weeks.

The reusable report window offers exactly six user-facing choices: Today, Last 7 Days, Last 30 Days, Month to Date, Custom, and All Time. Reversed valid Custom bounds are normalized before evaluation and the normalized bounds are returned to the client. Malformed dates retain the existing graceful behavior and do not produce an HTTP 400 response.

### Accepted Ready handoff

For a canonical completed case, finalized reporting attribution comes from the accepted Ready handoff defined by issue #111:

- it is the latest successful Ready-for-Doctor handoff that was not withdrawn and subsequently led to Doctor Arrived;
- Doctor Arrived accepts that handoff and does not create a new assignment snapshot;
- withdrawn handoffs remain auditable but do not classify the completed case;
- a reissued Ready creates a different reporting candidate and a new Ready-wait interval.

The accepted Ready snapshot supplies canonical completed-case attribution for:

- doctor;
- procedure;
- sedation status;
- confirmed expected allocation.

Add-on remains separate scheduling-context metadata and does not change this dispatch attribution boundary.

### Legacy completed cases

Legacy completed cycles may lack a truthful durable Ready handoff or Ready timestamp.

For those cases:

- keep using the existing finalized historical assignment stored for the completed cycle;
- do not fabricate or infer a Ready timestamp;
- allow the case to remain in otherwise valid completed-case populations;
- exclude it from metrics whose definition requires a truthful accepted Ready timestamp.

A legacy-only day therefore cannot establish an Observed Doctor Day under the Ready-anchored observed-flow model.

## Reporting populations

The redesign preserves the existing population partition rather than treating every historical record as analytically interchangeable.

Conceptually distinguish:

- normal completed history that remains visible in normal reporting;
- the standard included completed population used by analytical aggregates;
- reporting-excluded completed records that remain visible but do not enter standard aggregates;
- manual-review exceptions that remain outside normal reporting;
- aborted or incomplete episodes that remain outside completed-case throughput;
- metric-specific contributing subsets that require additional truthful timestamps or allocation data.

### Practice Completed Cases

The Practice Overview `Completed Cases` headline preserves the existing normal completed count.

That means:

- manual-review exceptions remain outside the normal count;
- reporting-excluded completed records still count as truthfully completed cases;
- Data Quality explains that some of those completed cases were excluded from analytical aggregates.

The Practice count should therefore reconcile to:

`Completed Cases = Included Completed Cases + Reporting-Excluded Completed Cases`

within the current report scope.

Exclusion changes calculations, not the historical fact that a case completed.

### Analytical denominators

The standard included completed population remains the default denominator for analytical surfaces such as:

- Procedure Mix;
- procedure baselines;
- doctor observed-flow summaries;
- Schedule Fit;
- completed-case trends.

Doctor- or procedure-scoped analytical `Completed Cases` counts use the corresponding scoped standard included population unless a surface explicitly labels a broader audit/completed population.

The reusable report query separates population selection from aggregation:

- `Window` selects the whole-day UTC reporting window.
- `Scope` selects Practice or a Doctor ID, plus All, Sedation, or Non-sedation cases.
- `ProcedureGrouping` selects Procedure Family or Detailed Variant without changing population membership.

Historical doctor IDs remain valid Doctor scopes even when the doctor is no longer active in the current assignment roster. Historical report accessibility must not depend on current assignment eligibility.

A truthfully completed phase may contribute to a phase-duration metric once its truthful endpoint exists when existing reporting semantics already allow that. It must not be promoted into completed throughput merely because an earlier phase finished.

## Core operational metrics

### Ready Wait

`Ready Wait` is the elapsed time from the accepted Ready handoff to Doctor Arrived:

`AcceptedReadyAt -> DoctorArrivedAt`

Rules:

- only the accepted, non-withdrawn Ready handoff contributes;
- withdrawn Ready intervals never contribute;
- reissued Ready starts a new candidate interval;
- a case without a truthful accepted Ready timestamp has no Ready Wait observation;
- a missing observation is not represented as zero.

Ready Wait describes the post-handoff wait after the room formally declared itself ready for the doctor.

### Seated -> Doctor

`Seated -> Doctor` is the elapsed time:

`SeatedAt -> DoctorArrivedAt`

It describes the total patient-seated interval before Doctor Arrived, including prep time before the formal Ready handoff. Ready withdrawals and reissues do not rewrite its start point.

### Doctor Time

`Doctor Time` is:

`DoctorArrivedAt -> DoctorCompleteAt`

It is not interchangeable with measured case-flow allocation time used by Schedule Fit.

### Turnover

`Turnover` is:

`DoctorCompleteAt -> RoomAvailableAt`

It contributes only when both endpoints truthfully exist.

### Median and average prominence

For prominent operational timing cards and primary timing trends:

- median is the headline statistic;
- average may appear as secondary/detail context;
- average must not receive equal or greater visual prominence than the corresponding median in the primary summary surface;
- contributing sample size must remain available.

A no-observation population renders as an explicit empty or unavailable state, not a measured `0` duration.

## Observed Doctor Day model

### Purpose

The Observed Doctor Day model describes only doctor-flow activity ChairSide actually observed. It is not a schedule, attendance record, timecard, productivity measure, or statement about total hours worked.

### Qualifying observed-flow case

A case qualifies for the Ready-anchored observed doctor-flow model when all of the following are true after current report scope is applied:

- the case belongs to the scoped standard included completed population;
- it is attributed to the selected doctor;
- it has a truthful accepted Ready timestamp;
- it has truthful Doctor Arrived and Doctor Complete timestamps;
- the timestamps are ordered `AcceptedReadyAt <= DoctorArrivedAt <= DoctorCompleteAt`;
- Accepted Ready and Doctor Complete fall on the same UTC calendar day.

Cross-day or invalid timing records do not silently create a multi-day span. They remain governed by reporting-exception and Data Quality rules.

### Observed Doctor Day

An `Observed Doctor Day` exists for a doctor and UTC calendar date only when at least one qualifying observed-flow case exists for that doctor on that date.

Consequences:

- no qualifying activity means no doctor-day observation;
- unobserved days are omitted from doctor-day counts, averages, medians, and trends;
- unobserved days are never zero-filled;
- reporting must not label an unobserved day as absent, off, unavailable, unscheduled, or idle;
- legacy-only days without truthful Ready timestamps do not establish an Observed Doctor Day.

The observed-day case count is the count of qualifying observed-flow cases on that doctor-day. Completed cases valid for other reporting but unable to satisfy the Ready-anchored definition remain available to those other metrics rather than being forced into this model.

### Observed Clinical Span

For one Observed Doctor Day:

- start = earliest accepted Ready timestamp among qualifying cases;
- end = latest Doctor Complete timestamp among qualifying cases;
- `Observed Clinical Span = end - start`.

Observed Clinical Span is elapsed wall-clock time, not a sum of case durations.

It describes the observed clinical-flow window between the first formal Ready handoff and the last Doctor Complete on that same observed day.

It must not be described as:

- hours worked;
- scheduled hours;
- attendance;
- productivity;
- provider availability.

### Doctor Working intervals

Each qualifying observed-flow case contributes one Doctor Working interval:

`[DoctorArrivedAt, DoctorCompleteAt)`

Use half-open interval semantics for deterministic boundaries. If one interval ends at the exact instant another begins, those intervals do not overlap.

All intervals are clipped to the Observed Clinical Span before wall-clock aggregation. Invalid or non-positive intervals do not contribute and remain visible through applicable Data Quality/audit behavior rather than becoming zero-duration work.

### Wall-clock union and concurrency

`Observed Doctor Working Time` is the wall-clock union of all qualifying Doctor Working intervals inside the Observed Clinical Span.

Overlapping intervals count once as elapsed working time.

Partition each span by simultaneous active Doctor Working intervals:

- exactly 1 active room;
- exactly 2 active rooms;
- 3 or more active rooms;
- 0 active rooms, represented as Unstructured Time.

Use interval-union/sweep-line logic or an equivalent deterministic algorithm.

These are wall-clock buckets, not weighted room-minutes. Ten minutes with two active intervals contributes 10 minutes to the `2 rooms` bucket, not 20.

`Peak Concurrent Rooms` is the maximum simultaneous active Doctor Working interval count inside the span.

### Unstructured Time

`Unstructured Time` is the portion of an Observed Clinical Span during which ChairSide observes no active Doctor Working interval for that doctor.

Equivalent definitions are:

`Unstructured Time = Observed Clinical Span - Observed Doctor Working Time`

or the wall-clock bucket where active Doctor Working interval count equals zero.

The full partition must satisfy:

`Unstructured + 1-room + 2-room + 3+-room = Observed Clinical Span`

subject only to documented duration precision.

Unstructured Time must not be described as:

- idle time;
- unproductive time;
- unused time;
- available time;
- unscheduled time;
- absent time;
- recoverable capacity.

ChairSide does not know what occurred outside the room events it observed.

## Procedure Mix and Procedure Intelligence

### Procedure Mix denominator

Procedure Mix answers what kinds of completed cases are represented in the current analytical scope and in what proportion.

At Practice scope:

- denominator = all cases in the current scoped standard included completed population;
- numerator = completed cases in the selected procedure grouping;
- percentage = numerator / denominator.

At Doctor scope:

- denominator = all cases for the selected doctor in the current scoped standard included completed population;
- numerator = that doctor's completed cases in the selected procedure grouping;
- percentage = numerator / denominator.

Counts and percentages are presented together. The contributing sample size remains visible.

Displayed rounding may prevent percentages from summing to exactly 100 percent, but underlying case groups must reconcile to the scoped denominator.

### Procedure family, detailed variant, and sedation

Preserve both recognized grouping lenses:

- Procedure Family folds sedation variants into the base procedure family.
- Detailed Variant keeps variants such as `EXT` and `EXT+SED` distinct.

Sedation remains a modifier of the primary procedure, never a second case and never a separately timed procedure.

The reusable report query owns Procedure Family versus Detailed Variant as aggregation behavior and Sedation as a population filter. Issue #215 owns Practice and Doctor Procedure Mix presentation.

### Procedure Intelligence

Procedure Intelligence may describe, for a sufficiently supported scoped procedure population:

- case count;
- median Doctor Time;
- typical observed timing range;
- expected allocation as scheduling context;
- doctor x procedure drill-down;
- sedation context.

Longer or shorter observed duration must not be framed as inherently better or worse.

The exact percentile or quantile rule for `Typical Observed Range` is deferred to #218.

## Schedule Fit

### Purpose

Schedule Fit compares scheduling assumptions with observed case flow.

It evaluates the scheduling model, not provider performance.

### Expected allocation basis

For canonical cases, expected allocation comes from confirmed expected allocation captured by the accepted Ready handoff.

For legacy completed cases without a canonical accepted Ready snapshot, preserve existing finalized historical allocation attribution when otherwise valid. Do not invent a Ready timestamp to support it.

### Observed measurement basis

The approved first-version Schedule Fit comparison preserves the existing measured case-flow interval:

`SeatedAt -> DoctorCompleteAt`

This is intentionally different from Doctor Time.

Do not substitute `DoctorArrivedAt -> DoctorCompleteAt` and continue calling the result allocation variance or Schedule Fit. A future change to the measured allocation basis requires an explicit design decision and characterization coverage.

### Contributing population

A case contributes to Schedule Fit when:

- it belongs to the current scoped standard included completed population;
- it has a valid positive confirmed expected allocation;
- the observed measured case-flow interval can be calculated truthfully.

Cases in the wider included population that cannot satisfy those requirements remain part of visible population context but do not contribute fabricated expected or observed minutes.

### Variance, slack, debt, and net

For one contributing case:

`variance = observed measured case-flow - expected allocation`

`slack = max(expected allocation - observed measured case-flow, 0)`

`debt = max(observed measured case-flow - expected allocation, 0)`

Across a population:

- total expected = sum of contributing expected allocation;
- total observed = sum of contributing measured case-flow;
- total slack = sum of case-level slack;
- total debt = sum of case-level debt;
- net variance = total observed - total expected;
- equivalently, net variance = total debt - total slack.

Slack and debt remain separate even when signed net variance balances. Opposing mismatches must not disappear behind a zero net.

### Blocks and coverage

Scheduling blocks are a reporting lens over minute totals, not a separate lifecycle fact. The current default reporting block is 10 minutes.

Schedule Fit exposes both:

- scoped included completed-case count;
- Schedule Fit contributing case count.

Coverage is contextual evidence, not a score. Neutral presentation may state `N of M cases with valid allocation data` or equivalent.

### Calibration Insights

A `Calibration Insight` is a neutral evidence-backed callout that a scheduling assumption appears persistently over- or under-allocated in a sufficiently supported population.

Calibration Insights:

- identify patterns for human review;
- show or expose their contributing sample;
- use compatible expected and observed timing semantics;
- preserve over-allocation and under-allocation as distinct directions;
- remain subtle rather than warning/error styled for ordinary calibration differences;
- may prioritize scheduling assumptions for review but never rank providers;
- never automatically change expected allocation;
- remain silent when evidence is insufficient or no notable pattern exists.

Issue #219 owns the exact first-version rules for:

- minimum qualifying sample size;
- material-deviation threshold;
- directional-consistency requirement;
- `At expected` tolerance;
- whether persistence across multiple historical periods is initially required.

The exploratory values in #219 are hypotheses, not canonical thresholds.

## Sample size and no-observation behavior

Every segmented analytical surface retains visible contributing sample context.

General rules:

- narrowed filters reduce both numerator and denominator to selected scope;
- do not reuse an unfiltered denominator for a filtered percentage or comparison;
- an empty population is not a measured zero;
- an unobserved doctor-day is not a zero-hour doctor-day;
- a metric whose required timestamps are absent has no observation for that case;
- raw descriptive counts may remain visible when truthful even when a population is too small for interpretive language;
- weak samples suppress unsupported comparisons and insights rather than manufacturing certainty.

The general descriptive sample guardrail is:

- `N = 0`: Empty, rendered to users as `No observation`;
- `N = 1-4`: Limited;
- `N >= 5`: Sufficient.

A metric with a nonempty wider scoped population but zero contributing observations is Unavailable, not a measured zero. Every population in a comparison must be Sufficient before comparison language is shown. This rule is a descriptive presentation guardrail, not a statistical-significance claim.

Calibration Insight sample rules remain separately owned by #219 because their evidentiary threshold may be stricter than ordinary descriptive reporting.

## Data Quality and audit

### Healthy state

- no prominent warning or celebratory healthy badge is required;
- the report may remain visually quiet.

### Limited or excluded state

- expose concise context when records were excluded or a population is incomplete for a metric;
- keep included/excluded counts available where they materially affect interpretation;
- do not imply an excluded record disappeared from history.

### Action-required state

- elevate pending manual-review exceptions or other conditions requiring human action;
- keep reason and review path available without polluting unrelated healthy surfaces.

### Audit evidence

Case audit is the evidence layer behind summary metrics and insights.

The action-required Review Queue remains global within the selected reporting date window. Doctor and Sedation analytical scope do not hide unresolved review items. The analytical Case Audit does inherit the selected Practice or Doctor and Sedation scope.

Drill-down must preserve the scope that produced a metric or insight so the user can inspect contributing cases without silently changing date, doctor, procedure, sedation, or inclusion context.

Excluded, legacy, and exception records remain truthfully visible in appropriate audit/review surfaces even when they do not contribute to standard aggregates.

No audit surface may add PHI or infer patient identity.

## Non-punitive interpretation constraints

Explicitly prohibited:

- doctor efficiency scores;
- provider performance grades;
- leaderboards or best/worst rankings;
- attendance or absence inference;
- idle-time reporting;
- productivity quotas;
- staff shame language;
- red/green provider grading;
- treating Unstructured Time as recoverable capacity;
- treating Schedule Fit debt as provider failure;
- treating Schedule Fit slack as automatically available appointment capacity;
- treating procedure duration as a quality judgment;
- automatically changing scheduling assumptions because a report found a pattern.

Operational comparisons are framed around observed flow, process variation, and scheduling-model calibration.

## Current implementation compatibility and known gaps

This design intentionally documents approved target semantics before production behavior changes.

At publication of #212:

- accepted Ready attribution, withdrawn-handoff exclusion, legacy no-fabrication rules, UTC windows, and population partitioning are established and remain authoritative;
- current report models already calculate median and average timing, but later UI work must make median prominent;
- current `ObservedDoctorDay` starts its clinical span at first Seated rather than first qualifying Ready;
- current observed-load concurrency uses Seated-to-Doctor-Complete room intervals rather than Doctor Working intervals;
- current observed-load reporting does not yet expose Unstructured Time under this definition;
- current Procedure Mix is doctor-scoped; Practice Procedure Mix is future work under #215;
- current Schedule Fit already preserves the Seated-to-Doctor-Complete measured case-flow basis and separate slack/debt math, while framing and Calibration Insights remain future work under #219;
- current Data Quality and exception detail exist, while progressive disclosure is future work under #220.

These are implementation gaps, not permission to reinterpret the canonical definitions in this document.

## Deferred design and future reporting

The following decisions are deliberately not fixed by #212:

- exact general sample-size guardrails and reusable limited-sample behavior - #213;
- exact percentile/quantile rule for Typical Observed Range - #218;
- Calibration Insight minimum sample size - #219;
- Calibration Insight material-deviation threshold - #219;
- Calibration Insight directional-consistency rule - #219;
- Calibration Insight `At expected` tolerance - #219;
- whether first-version Calibration Insights require persistence across multiple historical periods - #219.

Deferred analytical areas until enough production history exists and the core redesign is stable:

- Add-on cohort analysis;
- weekday/session analysis;
- time-of-day congestion patterns;
- richer lifecycle flow-decomposition visualization;
- broader Operational Insights beyond Calibration Insights.

No later issue should silently hard-code one of these deferred choices as if #212 approved it.

## Implementation and test derivation rules

Later implementation issues should derive these guards directly from this design:

- Practice Completed Cases must reconcile to included plus reporting-excluded normal completed cases.
- Ready Wait tests must prove withdrawn Ready intervals are excluded and legacy missing Ready is not zero-filled.
- prominent timing tests must prove median is primary and average is secondary/detail.
- Observed Doctor Day tests must cover no-activity omission, legacy-only days, same-day qualification, and cross-day handling.
- Observed Clinical Span tests must prove first accepted Ready to last same-day Doctor Complete.
- concurrency tests must cover single, sequential, overlapping, same-timestamp boundary, and 3+ interval cases without double-counting.
- Unstructured Time plus concurrency buckets must reconcile to Observed Clinical Span.
- Procedure Mix tests must reconcile counts and percentages to current scoped standard included denominators at Practice and Doctor scope.
- Schedule Fit tests must preserve compatible timing, separate slack/debt, signed net variance, and population coverage.
- Calibration Insight tests must suppress weak evidence and prove no schedule mutation occurs.
- Data Quality/audit tests must prove exclusions change calculations without erasing durable visibility and that drill-down preserves producing scope.
- presentation tests must reject provider ranking, attendance inference, idle-time wording, and punitive framing where those strings or structures are under application control.

## Delivery relationship

This document is the hard design gate for #211.

Dependency order remains:

1. #212 - canonical semantics;
2. #213 - shared scope, filters, and sample-size guardrails;
3. #214 through #220 - independent analytical and presentation slices where practical;
4. #221 - integrated regression and browser acceptance.

No production UI, API, persistence, or reporting calculation changes are part of #212 itself.
