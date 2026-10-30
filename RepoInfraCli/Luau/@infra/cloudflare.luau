--!strict
-- Type stubs; RepoInfraCli provides the implementation at runtime.
local types = require("@infra/types")

type Output<T> = types.Output<T>
type Input<T> = types.Input<T>

export type RecordType = "A" | "AAAA" | "CNAME" | "TXT" | "MX" | "CAA"

export type RecordOptions = {
    type: RecordType,
    content: Input<string>,
    proxied: boolean?,
    ttl: number?,
    protect: boolean?,
}

export type Record = {
    id: Output<string>,
    hostname: string,
}

export type Zone = {
    id: Output<string>,
    name: string,
    record: (name: string) -> (options: RecordOptions) -> Record,
}

export type ProviderOptions = {
    api_token: types.Secret,
}

export type Provider = {
    zone: (name: string) -> Zone,
}

local cloudflare = {}

cloudflare.ip_ranges = {} :: { string }

function cloudflare.provider(options: ProviderOptions): Provider
    error("native")
end

return cloudflare
