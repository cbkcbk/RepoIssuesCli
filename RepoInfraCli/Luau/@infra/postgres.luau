--!strict
-- Type stubs; RepoInfraCli provides the implementation at runtime.
local types = require("@infra/types")

export type DatabaseOptions = {
    version: string,
    password: types.Secret,
    network: types.Network,
    data: string,
    user: string?,
}

export type Database = types.Database

export type BackupOptions = {
    bucket: types.Bucket,
    credentials: types.Secret,
    schedule: string,
    prefix: string?,
}

local postgres = {}

function postgres.database(name: string): (options: DatabaseOptions) -> Database
    error("native")
end

function postgres.server(database: Database): types.ComposeService
    error("native")
end

function postgres.backup(database: Database): (options: BackupOptions) -> types.ComposeService
    error("native")
end

return postgres
