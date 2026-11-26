# P<n> <part name>

**What it is**: <what runs or ships, and where> · **Maturity**: hypothesis · **Code**: <paths>
<!-- Subpart only: use its own ID (e.g. P2.1) in the heading and add **Parent**: P2 here.
One level only. Keep the subpart's obligations here, not in its parent. -->

## Why it exists

<What it does for the journeys, R and Q its architecture parts-table row cites.>

## Components

<!-- Optional: a mermaid flowchart of the components and what flows between them.
One section per component. Its behaviors and invariants sit under it and keep the part's
numbering. Past the budget, move a component to parts/p<n>-<slug>/<component>.md. -->

### <component>

\<What it does for the part.>

- **P<n>.B-01 <title>**: <observable behavior>. Test: <name or unenforced>.
- **P<n>.I-01 <title>**: <always true>. Enforced by: <test, analyzer, metric or unenforced>.

## Interfaces

- <Interface>: <compatibility promise, failure meaning>. Schema: <link to code>.

## Failure

- <When a dependency fails, what this part does>. Users see: J-01, Variations.

## Budgets

- Q-01: <this part's share of its band>.

## Left open

- <...>
