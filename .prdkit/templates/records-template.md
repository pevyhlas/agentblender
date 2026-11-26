# Records

<!-- One line each, newest first. Record a HOLD or WAIVER only when the owner decided it;
never invent one.
Keep the owner's full operative scope and end condition in the row. Ask the owner before
applying a narrower or wider reading. The reason is the verbatim quote with its source and
date, or the checked ref= printed by authority-ref.sh (see authority-template.md).
A HOLD's until= date or condition is a reminder: remove the row only when the owner explicitly
lifts the named hold. A WAIVER expires; an owner renewal is a new row, not an edit to expires=.
Deploy workflows, or the agent that observed a deployment, add DEPLOY lines. A value runs
to the next key=; the reason follows " — ".
HOLD   <id> owner=<role> scope=<part|env|change> since=<date> until=<date|condition> — <reason>
WAIVER <id> owner=<role> check=<acceptance item> scope=<scope> expires=<date> — <reason>
ATTEST <id> by=<owner|agent> what=<claim> env=<env> version=<rev> date=<date> — <evidence link>
DEPLOY <app> env=<env> rev=<commit> date=<date> by=<workflow run link>
DEPLOY <app> env=<env> rev=none date=<date> by=<owner|agent> — <why nothing is deployed there> -->
