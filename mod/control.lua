local U = require("scripts.util")
local Actor = require("scripts.actor")
local Actions = require("scripts.actions")
local Operations = require("scripts.operations")
local Observation = require("scripts.observation")
local Catalog = require("scripts.catalog")
local Pilot = require("scripts.pilot")
local Visibility = require("scripts.visibility")
local Recovery = require("scripts.recovery")
local FactorySnapshot = require("scripts.factory_snapshot")
local Spatial = require("scripts.spatial")
local Research = require("scripts.research")
local Rocket = require("scripts.rocket")

script.on_init(Actor.initialize)
script.on_configuration_changed(Actor.initialize)
-- No on_load mutations: persisted LuaEntity references and records are restored by the engine.

local function hello(args)
  local s = Actor.state()
  local restore_pause
  if s.awaitingController then restore_pause = s.checkpointWasPaused end
  local session = U.string(args.sessionId, "sessionId")
  local world = s.worldId or U.string(args.worldId, "worldId (required on first hello)")
  if args.worldId then U.check(args.worldId == world, "world_mismatch", "The loaded save belongs to a different world") end
  if s.awaitingController then
    U.check(args.checkpointId == s.checkpointId, "checkpoint_mismatch", "Resume must acknowledge the prepared checkpoint")
    U.check(session ~= s.sessionId, "new_session_required", "Resume requires a fresh controller session")
  end
  if not s.freeplayConfigured then
    -- The one agent receives the normal no-intro kit. Connecting its pilot must not
    -- generate another kit or a late crash site in an already running factory.
    U.check(remote.interfaces.freeplay ~= nil, "unsupported_scenario", "The agent currently requires base freeplay")
    remote.call("freeplay", "set_created_items", {})
    remote.call("freeplay", "set_respawn_items", {})
    remote.call("freeplay", "set_disable_crashsite", true)
    remote.call("freeplay", "set_skip_intro", true)
    s.freeplayConfigured = true
  end
  s.worldId = world
  if session ~= s.sessionId then
    Operations.cancel_active("Controller session changed")
    s.sessionId, s.generation = session, s.generation + 1
  end
  if s.incarnation == 0 then Actor.spawn(true) end
  s.awaitingController = false
  if restore_pause ~= nil then game.tick_paused = restore_pause end
  Pilot.ensure_attached()
  return {scope = Actor.scope(), capabilities = Actions.capabilities,
    actorAlive = Actor.get() ~= nil, controlMode = s.controlMode, protocolVersion = 1,
    gameVersion = script.active_mods.base, receiptCapacity = Operations.capacity}
end

local handlers = {hello = hello, observe = Observation.observe, research_state = Research.observe, rocket_state = Rocket.observe, factory_snapshot = FactorySnapshot.page, submit = Operations.submit,
  spatial = Spatial.observe, validate_placement = Spatial.validate_placement,
  prepare_checkpoint = function(args)
    local s = Actor.state()
    if not s.awaitingController then
      local id = U.string(args.checkpointId, "checkpointId")
      Operations.cancel_active("Controller preparing a checkpoint")
      U.check(not s.stopUnconfirmed, "stop_unconfirmed", "Character stop is unconfirmed")
      Actor.stop(false)
      -- The engine disconnects saved players when loading a headless server. Detach
      -- before sealing so that transition cannot change the saved control scope.
      if s.pilotIndex then Pilot.before_leave{player_index = s.pilotIndex} end
      s.checkpointWasPaused = game.tick_paused
      game.tick_paused = true
      s.generation, s.awaitingController, s.checkpointId = s.generation + 1, true, id
      s.checkpointTick = game.tick
    end
    return {scope = Actor.scope(), checkpointId = s.checkpointId, preparedTick = s.checkpointTick,
      awaitingController = true, operation = Operations.last_receipt()}
  end,
  mark_fixture = function(args)
    local reason = U.string(args.reason, "reason")
    local s = Actor.state()
    s.fixture = true
    s.fixtureTick = s.fixtureTick or game.tick
    s.fixtureReason = s.fixtureReason or reason
    return {fixture = true, reason = s.fixtureReason, markedTick = s.fixtureTick}
  end,
  operation = function(args) return Operations.get(args.operationId) end,
  cancel = function(args) return Operations.cancel(args.operationId) end,
  recipes = Catalog.recipes, technologies = Catalog.technologies, production_catalog = Catalog.production}

remote.add_interface("factorio_agent", {execute = function(json)
  local request, response
  local ok, data = pcall(function()
    U.check(type(json) == "string" and #json <= 65536, "invalid_request", "JSON request exceeds 64 KiB or is not a string")
    request = helpers.json_to_table(json)
    U.check(type(request) == "table", "invalid_json", "A JSON object is required")
    U.check(request.protocolVersion == 1, "protocol_version", "Only protocolVersion 1 is supported")
    U.string(request.requestId, "requestId")
    U.check(type(request.action) == "string" and handlers[request.action], "unknown_action", "Unsupported RPC action")
    U.check(type(request.arguments) == "table", "invalid_arguments", "arguments must be an object")
    U.check(storage.agent ~= nil, "not_initialized", "Mod storage has not been initialized")
    return handlers[request.action](request.arguments)
  end)
  response = {protocolVersion = 1, requestId = type(request) == "table" and request.requestId or "", ok = ok,
    tick = game.tick, data = ok and data or {}, error = not ok and U.error(data) or nil}
  local encoded, result = pcall(helpers.table_to_json, response)
  if encoded then return result end
  return helpers.table_to_json{protocolVersion = 1, requestId = response.requestId, ok = false, tick = game.tick,
    data = {}, error = {code = "serialization_error", message = tostring(result)}}
end})

script.on_event(defines.events.on_tick, function()
  local s = Actor.state()
  if not s then return end
  local character = Actor.get()
  if character and not s.pilotIndex and game.tick % 60 == 0 then Visibility.refresh(character) end
  if s.respawnTick and game.tick >= s.respawnTick and not Actor.get() then
    local ok, err = pcall(Actor.spawn, false)
    if not ok then s.respawnError, s.respawnTick = U.error(err), game.tick + 600 end
    if ok then Pilot.ensure_attached() end
  end
  Operations.tick()
end)

script.on_event(defines.events.on_entity_died, function(event)
  local s = Actor.state()
  if not s then return end
  if event.entity == s.character then
    local record = Operations.active()
    if record then Operations.finish(record, "failed", {code = "actor_dead", message = "The character was killed"}) end
    Actor.on_death(event.entity)
  end
  if event.entity.unit_number then s.known[tostring(event.entity.unit_number)] = nil end
end)

script.on_event(defines.events.on_player_joined_game, Pilot.ensure_attached)
script.on_event(defines.events.on_post_entity_died, Recovery.register)
script.on_event(defines.events.on_player_created, Pilot.created)
script.on_event(defines.events.on_gui_click, Pilot.click)
script.on_event(defines.events.on_pre_player_left_game, Pilot.before_leave)
script.on_event(defines.events.on_player_died, Pilot.player_died)
