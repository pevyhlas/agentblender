# authz-diff: local settings (example)

Copy this file to `references/local.md` in the vendored skill and edit it. Library updates never
read, write or remove `local.md`.

One name per line. A name is a method or attribute name, without arguments.

`auth-convention:` an extension method on an endpoint or group, or an attribute, that requires
authorization. Endpoints that carry it are `authorized` at that level, and removing it is an
`auth-removed` fact:

- auth-convention: RequireScope
- auth-convention: RequireTenantMember

`anonymous-convention:` one that allows anonymous access. Endpoints that carry it are
`anonymous`, and adding it is an `anonymous-added` fact:

- anonymous-convention: AllowPublicProbe

`handler-check:` a call inside a handler that checks access. It is listed under
`handler_checks`; it does not change the coverage state, because the script cannot tell whether
every path through the handler reaches it:

- handler-check: EnsureCallerOwns
