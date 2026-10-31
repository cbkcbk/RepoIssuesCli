--!strict
-- Type stubs; RepoInfraCli provides the implementation at runtime.
local types = require("@infra/types")

export type ProviderOptions = {
    url: string,
    ref: string,
    path: string?,
}

export type Provider = {
    host: (name: string) -> types.Host,
    role: (name: string) -> types.Role,
    network: (name: string) -> types.Network,
    database: (name: string) -> types.Database,
}

local repo = {}

function repo.provider(options: ProviderOptions): Provider
    error("native")
end

return repo
