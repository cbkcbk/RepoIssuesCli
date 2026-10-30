--!strict
-- Type stubs; RepoInfraCli provides the implementation at runtime.
local types = require("@infra/types")

export type ContainerOptions = {
    project: string,
    registry: string,
    tag: string,
    configuration: string?,
    runtime: string?,
}

local dotnet = {}

function dotnet.container(options: ContainerOptions): types.Build
    error("native")
end

return dotnet
