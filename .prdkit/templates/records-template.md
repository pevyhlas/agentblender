# Records

<!-- One line each, newest first. Agents record a HOLD or WAIVER only when the owner decided it,
with the owner's quote and date in the reason; never invent one. Deploy workflows, or the agent
that observed a deployment, add DEPLOY lines. A value runs to the next key=; the reason follows
" — ".
HOLD   <id> owner=<role> scope=<part|env|change> since=<date> until=<date|condition> — <reason>
WAIVER <id> owner=<role> check=<acceptance item> scope=<scope> expires=<date> — <reason>
ATTEST <id> by=<owner|agent> what=<claim> env=<env> version=<rev> date=<date> — <evidence link>
DEPLOY <app> env=<env> rev=<commit> date=<date> by=<workflow run link>
DEPLOY <app> env=<env> rev=none date=<date> by=<owner|agent> — <why nothing is deployed there> -->
