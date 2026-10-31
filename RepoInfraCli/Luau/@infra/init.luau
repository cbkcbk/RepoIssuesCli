--!strict
-- Type stubs; RepoInfraCli provides the implementation at runtime.
local types = require("@infra/types")

export type Secret = types.Secret
export type Output<T> = types.Output<T>
export type Input<T> = types.Input<T>
export type FileRef = types.FileRef
export type Host = types.Host
export type App = types.App
export type Build = types.Build
export type Role = types.Role
export type FirewallRule = types.FirewallRule
export type Network = types.Network
export type Database = types.Database
export type Exports = types.Exports

local infra = {}

infra.git = {
    sha = "",
    branch = "",
}

function infra.secret(path: string): Secret
    error("native")
end

function infra.file(path: string): FileRef
    error("native")
end

-- Ownership marker on live resources: infra:<project> [env=<env>] name=<name> [protect] [exported]
function infra.project(name: string): (options: types.ProjectOptions) -> ()
    error("native")
end

function infra.host(name: string): (options: types.HostOptions) -> Host
    error("native")
end

function infra.app(name: string): (options: types.AppOptions) -> App
    error("native")
end

function infra.play(name: string): (options: types.PlayOptions) -> ()
    error("native")
end

return infra
