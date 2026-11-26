---
name: authz-diff
description: "Lists what a change does to authorization in a dotnet web app (minimal API endpoints, controller actions and Razor pages added, changed or removed, each with the authorization it visibly has at endpoint, group, controller, page or global level; removed authorize metadata, added anonymous access, policy, cross-origin and authentication scheme edits), and marks what it cannot see as unknown with the reason. Use when reviewing a dotnet change for missing or weakened authorization; not for deciding whether an endpoint should be public."
metadata:
  prdkit/needs: python3 git
  estate/requires-kit: ">=3.1.5"
allowed-tools: Bash(${CLAUDE_SKILL_DIR}/scripts/authz_diff.py *)
---

# authz-diff

A script reads the C#, Razor and appsettings files of a change and lists facts with `file:line`:
which endpoints were added, changed or removed, and what authorization each one visibly has. It
does not decide whether an endpoint should be protected, and it never guesses: when something
that decides coverage is out of its sight, the endpoint is `unknown` with the reason. You judge
each fact against the change, and you own the verdict.

## Run it

Run the script itself (not through `python3`). Give the pull request's range, or a unified diff
(git or plain `diff -u`) when there is no git history (a review package's diff, for example):

```bash
${CLAUDE_SKILL_DIR}/scripts/authz_diff.py --root <repo> --base origin/main --head HEAD
${CLAUDE_SKILL_DIR}/scripts/authz_diff.py --root <repo> --base origin/main --head HEAD --json
${CLAUDE_SKILL_DIR}/scripts/authz_diff.py --root <tree at the head> --diff pr.diff
```

The range starts at the merge base of `--base` and `--head`, as a pull request does; both trees
are read from git objects, including files the change does not touch (callers, base classes,
`_Imports.razor`, the global setup). `--all` lists every endpoint at `--head` as added, for an
inventory. The markdown summary is at most 60 lines; `--json` holds every fact. Exit 0 means the
facts were computed, whatever they are; exit 2 is a usage error, an unknown revision or a diff
with no file section.

To hand the facts to a reviewer that cannot run scripts, write both files into the change's
evidence directory under these file names, which the reviewer looks for:
`--out .prdkit/cache/evidence/<id>/facts/authz-diff.md --json-out .prdkit/cache/evidence/<id>/facts/authz-diff.json`.
Attach them to the Product-Kit review package, which keeps them under `evidence/`:
`build-review-package.sh <id> <base> <head> --evidence .prdkit/cache/evidence/<id>/facts/authz-diff.md --evidence .prdkit/cache/evidence/<id>/facts/authz-diff.json`.

Every report names what it was computed on: `revision.diff_from` (the merge base) and
`revision.head_commit`, plus each changed file's git blob ids (`files[]`), or
`revision.diff_sha256` for a diff. When the facts are already in the review package, cite them
only if that head is the head under review or the blob ids match; otherwise run the script
again, or say under Unverified that the facts are for another revision.

## Facts

| id | what the script saw |
|---|---|
| `endpoint-added` | a new `Map{Get,Post,Put,Delete,Patch,Methods,Fallback,Hub,...}` endpoint, controller action or `@page`, with method, route and coverage |
| `endpoint-changed` | an endpoint whose lines changed (`lines`) or whose coverage changed (`coverage`, with `coverage_before`) |
| `endpoint-removed` | an endpoint at the base that is gone at the head |
| `auth-removed` | a removed line with `RequireAuthorization`, `[Authorize]`, `AuthorizeFilter`, `AuthorizeView`, `AuthorizeRouteView`, a page convention or `UseAuthorization` |
| `anonymous-added` | an added `AllowAnonymous` (attribute, call or page convention) |
| `policy-changed` | an added or removed policy or requirement line (`AddPolicy`, default or fallback policy, `RequireRole`, `RequireClaim`, `RequireAssertion`, handlers) |
| `cors-changed` | an added or removed cross-origin line (`AddCors`, `UseCors`, `WithOrigins`, ...), in code or a `Cors` key in appsettings |
| `authn-scheme-changed` | an added or removed authentication scheme, default scheme or token validation line (`= false` is flagged), in code or an authentication key in appsettings |

Coverage, from what is visible at that revision:

| state | meaning |
|---|---|
| `anonymous(level)` | allows anonymous access at some level; it wins over authorization metadata |
| `authorized(level)` | `RequireAuthorization`, `[Authorize]` or a local convention at a level: `endpoint`, `handler`, `group`, `controller`, `base X`, `page`, `code-behind`, `imports`, `mount`, `global filter`, `hub class` or `service class` (the `T` of `MapHub<T>`/`MapGrpcService<T>`; a gRPC service method that allows anonymous access gives `anonymous(service method)`) |
| `fallback` | no metadata and no `ShortCircuit()` on the endpoint, its groups or its mount (that skips the authorization middleware, so it gives `unknown`); no `UseMvc`, and for every `UseEndpoints` call in the app's project, in a shared project with no host of its own, or in any project when the endpoint's own project has no host, an unconditional `app.UseAuthorization();` statement comes before it in the same block (not under `if`, `#if`, or in a `UseWhen`/`Map` branch; a minimal-hosting app that calls `UseAuthorization()` in no file of its project gets it added); and every app that maps the endpoint sets the fallback policy to a new policy or `DefaultPolicy`, never reset to null, in startup code that always runs (top-level statements, a static `Main`, a Startup class's `ConfigureServices`/`Configure` when the class is used without a condition (`UseStartup<T>`, `new T()`) or, when nothing uses it, only if nothing else in the tree builds a host, an options class's `Configure` when it is registered without a condition (`ConfigureOptions<T>`, `Add…<IConfigureOptions<…>, T>`); other mentions of the class are not uses, and when the class's project builds its own app, a use or call in another project does not count, or a helper every caller calls without a condition; a helper passed only by name, such as `AddAuthorization(Configure)`, is not followed and gives `unknown`) |
| `none-visible` | no authorization metadata found |
| `unknown(reason)` | something that decides it is out of sight, for example a group passed in by a caller that is not found, a lambda parameter, a handler in another file or declared twice, a base class declared twice in reach, a group variable assigned again, a convention inside an `if`, a group or endpoint variable used other than as `var.Convention(...)` (passed on, aliased, under a braceless `if`), a group returned by your own `Map...` method, conventions chained after your own `Map...` extension or after a call to a method that returns the endpoint or group, a `Map...` extension that adds conventions to the group it is given, conventional routes that differ, a `#if` line among attributes, a derived controller that allows anonymous access, an `@attribute` in `_ViewImports.cshtml`, an `AuthorizeFilter` or `AllowAnonymousFilter` not added as `Filters.Add(new ...)` in `AddControllers`/`AddMvc`/`Configure<MvcOptions>`, a fallback policy set under a condition or after an `if` that may return early, in a helper (a method or local function) that is called under a condition or not at all, or in a member the script does not read as a method, to something else or reset to null, a global filter or mount convention in such a helper, a `#if` line in an endpoint chain, a fallback set in only some of the apps that map the endpoint, callers that disagree, or a global setting in another or no project |

When the script sees something it cannot read that could allow anonymous access, an endpoint with
authorization is `unknown`, not `authorized`, and `seen` lists the authorization that was found.
Conventions you wrote that allow anonymous access are seen only when named in local settings
(otherwise minimal-API chains list them under `other_conventions`). Attribute classes you wrote
that derive directly from `AllowAnonymousAttribute` or implement `IAllowAnonymous` are read as
`anonymous`, as attributes or in `WithMetadata(new ...)`; one reached through another base of
yours is not seen, so look for it when an endpoint is `authorized`. An `authorized` endpoint carries
`also_unknown` only for things that could add authorization (call sites that agree on the state
but differ in policy, an `AuthorizeFilter` the script could not place); the counts list it as
`authorized (also unknown)`. An `anonymous` endpoint carries every unread reason as `also_unknown`.

A group passed to an extension method (`api.MapOrders()`) is followed to every call site in the
tree (up to 40), three levels deep; call sites in test paths are ignored. Conventions on
`MapControllers` apply to every action; those on `MapControllerRoute` or
`MapDefaultControllerRoute` only to actions without an attribute route (a `[Route]` on the
action, the controller, a partial part or a base class). The context section
lists the head's fallback policy, global `AuthorizeFilter`, authorization on `MapControllers`,
`MapRazorPages` or `MapRazorComponents`, `AuthorizeRouteView` and page conventions. A context
item applies to an endpoint in the same file, a call site's file or the same project (nearest
`.csproj`); one in another project is skipped for endpoints and pages, and is an `unknown` reason
for controllers, which a host project may serve. Partial class parts and base classes are matched
by namespace, and a handler by its class. Comments and strings in C#, `@code` blocks and Razor
comments are never read as metadata, and an `@attribute` inside an element is `unknown`. Facts in test paths carry `test_path: true` and are listed last.

A fact is evidence, not a finding. A `none-visible` endpoint may check access in its handler (the
fact lists `handler_checks` it saw, such as `AuthorizeAsync`); an `anonymous` one may be meant to
be public, like a health probe; an `authorized` one may use a policy too weak for the data. Read
the handler and decide; for `unknown`, follow the reason yourself or say under Unverified what
you could not confirm.

No fact is not the same as protected. The script does not see:
- authorization done in middleware, filters other than `AuthorizeFilter`, endpoint filters, or a
  custom convention not named in local settings (they show as `other_conventions`, which skip only
  the framework's describing conventions such as `WithName`, `WithTags` or `Produces`); controller
  application-model conventions (`options.Conventions.Add(...)`) are not read at all; a group
  returned by your own method whose name does not start with `Map` shows the same way, and the
  route then lacks that group's prefix;
- endpoints mapped with plain `Map(pattern, handler)`; actions declared on an `abstract` base
  controller are not listed at all, so read a changed base controller yourself; actions of a
  concrete base controller are listed only under the class that declares them (a derived
  controller that allows anonymous access makes them `unknown`), and an `override` action of your
  own base is `unknown`, since the base method's attributes also apply; an `override` of a
  framework method (`OnActionExecuting`, `ToString`) is listed as an action;
- in Blazor, whether the router uses `AuthorizeRouteView` (it is listed as context; with a plain
  `RouteView`, a page's `[Authorize]` is not checked when the router navigates interactively);
- routes or groups built at run time, or from constants in another file (shown as `{...}`);
- metadata passed as a variable (`.WithMetadata(attr)`; an `Add(...)` convention makes the endpoint
  `unknown`, as does `Finally(...)`), or attributes on a Razor `PageModel`
  declared outside the page's `.cshtml.cs`;
- what a policy requires, or whether a scheme is the one the policy uses;
- callers are matched by name: any call to a method of that name counts as a call to it, except
  that a static helper outside its class needs `Class.Method(` or `using static`; `new T()` counts
  as running `T`'s `Configure` methods, and a host started through your own generic helper
  (`Run<T>(...)`) is not read as a use of `T`;
- a class of your own named `AuthorizeAttribute` (outside test paths) makes every `[Authorize]`
  `unknown`, since which one applies is not read; another library's `AuthorizeAttribute` brought in
  by `using`, or a `using` alias of `AuthorizeAttribute` or `AllowAnonymousAttribute`
  (`using Open = ...AllowAnonymousAttribute;` then `[Open]`), is not seen; likewise a `using`
  alias of `AuthorizeFilter` or `AllowAnonymousFilter`, or a filter class of your own that
  implements `IAllowAnonymousFilter` or `IAuthorizationFilter`;
- a mount (`MapControllers()`, `MapRazorPages()`, `MapControllerRoute(...)`) whose value is kept in
  a variable, passed or returned is `unknown`, since conventions added to it elsewhere are not read;
- `UseAuthorization()` added inside a helper of your own (`app.UseSecurity()`) is not followed, so an
  app that calls `UseEndpoints` gives `unknown` rather than `fallback` (a minimal-hosting app too, when
  another file of its project calls `UseAuthorization()`); a fallback
  policy set only through `Configure<AuthorizationOptions>` with no authorization services is
  still read as `fallback`;
- a gRPC service method's attribute is read only from the service class itself and its partial parts,
  not from a method declared in a base service of your own;
- an attribute class of your own that derives from `AllowAnonymousAttribute` or implements
  `IAllowAnonymous` directly is read as `anonymous`; one that does so through another base of yours
  is not seen;
- a setup lambda kept in a variable (`Action<AuthorizationOptions> f = ...`) and applied under a
  condition: the fallback it sets is read as always set;
- authorization outside the app (ingress, gateway, network rules), and Razor page conventions
  matched to folders (listed as context, marked `unknown` on pages).

With `--diff`, the head is read from the tree under `--root` and the base is rebuilt from the
diff; callers and context come from that one tree for both sides, and `limits` says so.

## Local settings

`${CLAUDE_SKILL_DIR}/references/local.md` in this skill names the repository's own conventions (see
`${CLAUDE_SKILL_DIR}/references/local.example.md`): `auth-convention:` extension methods that require authorization,
`anonymous-convention:` ones that allow anonymous access, and `handler-check:` calls that count
as an access check inside a handler. Without the file only the framework's names are known.

<!-- rendered from skills/authz-diff/SKILL.md · do not hand-edit (source: agent-library) -->
