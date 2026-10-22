# P<n> <part name>

**What it is**: <what runs or ships, and where> · **Maturity**: hypothesis · **Code**: <paths>

## Why it exists

<The journeys, R and Q that need this part, and what it does for each.>

## Components

<!-- Optional: a mermaid flowchart of the components and what flows between them.
One section per component. Its behaviors and invariants sit under it and keep the part's
numbering. Past the budget, move a component to parts/p<n>-<slug>/<component>.md. -->

### <component>

<What it does for the part.>

- **P<n>.B-01 <title>**: <observable behavior>. Test: <name or unenforced>.
- **P<n>.I-01 <title>**: <always true>. Enforced by: <test, analyzer, metric or unenforced>.

## Interface guarantees

- <Interface>: <compatibility promise, failure meaning>. Schema: <link to code>.

## Failure semantics

- <What happens when a dependency fails; what the user sees.>

## Budgets

- <Latency, size or cost bands for this part.>

## Left open

- <...>
