local U = require("scripts.util")
local Actor = require("scripts.actor")
local Operations = require("scripts.operations")
local M = {}
local button_name = "factorio_agent_control_toggle"

function M.created(event)
  local s, player = Actor.state(), game.get_player(event.player_index)
  if not s or not s.freeplayConfigured or not player.character then return end
  s.newPilotCharacters = s.newPilotCharacters or {}
  s.newPilotCharacters[player.index] = player.character
end

local function empty_character(character)
  for _, id in pairs{defines.inventory.character_main, defines.inventory.character_guns,
    defines.inventory.character_ammo, defines.inventory.character_armor, defines.inventory.character_trash} do
    local inventory = character.get_inventory(id)
    if inventory and not inventory.is_empty() then return false end
  end
  return true
end

local function ai_permissions()
  local group = game.permissions.get_group("factorio_agent_ai_control")
  if group then return group end
  group = game.permissions.create_group("factorio_agent_ai_control")
  U.check(group ~= nil, "permission_group_failed", "Could not create the AI input permission group")
  for _, action in pairs(defines.input_action) do group.set_allows_action(action, false) end
  group.set_allows_action(defines.input_action.gui_click, true)
  group.set_allows_action(defines.input_action.toggle_show_entity_info, true)
  return group
end

function M.refresh()
  local s = Actor.state()
  for _, player in pairs(game.connected_players) do
    local button = player.gui.top[button_name]
    if not button then button = player.gui.top.add{type = "button", name = button_name} end
    button.caption = s.controlMode == "manual" and "Agent : Manuel → IA" or "Agent : IA → Manuel"
    button.tooltip = "Transférer le contrôle du même personnage. Toute prise manuelle marque la campagne assistée."
  end
end

function M.attach(player)
  local s, c = Actor.state(), Actor.get()
  U.check(c and player.connected, "actor_unavailable", "A living actor and a connected pilot are required")
  U.check(s.pilotIndex == nil or s.pilotIndex == player.index, "pilot_busy", "Another pilot is attached")
  if s.pilotIndex == player.index and player.character == c then return end
  Operations.cancel_active("Pilot attached to existing actor")
  s.previousPilotGroup, s.previousPilotForce = player.permission_group, player.force.name
  s.parkedCharacters = s.parkedCharacters or {}
  if player.character and player.character ~= c then
    local previous = player.character
    player.set_controller{type = defines.controllers.spectator}
    if s.newPilotCharacters and s.newPilotCharacters[player.index] == previous and empty_character(previous) then
      -- Remove only the empty temporary body just created for this pilot connection.
      -- Existing player characters and their possessions are preserved.
      previous.destroy()
    else
      s.parkedCharacters[player.index] = previous
    end
    if s.newPilotCharacters then s.newPilotCharacters[player.index] = nil end
  end
  player.force = c.force
  player.set_controller{type = defines.controllers.character, character = c}
  player.permission_group = ai_permissions()
  s.pilotIndex, s.controlMode, s.generation = player.index, "ai", s.generation + 1
  M.refresh()
end

function M.mode(player, mode)
  local s = Actor.state()
  if s.pilotIndex ~= player.index then M.attach(player) end
  Operations.cancel_active("Control mode changed to " .. mode)
  Actor.stop(true)
  if mode == "manual" then
    player.permission_group = s.previousPilotGroup
    s.humanInterventions = s.humanInterventions + 1
  else
    player.permission_group = ai_permissions()
  end
  s.controlMode, s.generation = mode, s.generation + 1
  M.refresh()
end

function M.click(event)
  if not event.element or not event.element.valid or event.element.name ~= button_name then return end
  local player = game.get_player(event.player_index)
  local ok, err = pcall(function()
    local mode = Actor.state().controlMode == "manual" and "ai" or "manual"
    M.mode(player, mode)
  end)
  if not ok then player.print("Factorio Agent : " .. U.error(err).message) end
end

function M.before_leave(event)
  local player = game.get_player(event.player_index)
  local s, c = Actor.state(), Actor.get()
  if not player or s.pilotIndex ~= player.index then return end
  Operations.cancel_active("Pilot disconnected; returning to standalone AI")
  Actor.stop(true)
  if c and player.character == c then player.set_controller{type = defines.controllers.spectator} end
  if c then c.associated_player = nil end
  player.permission_group = s.previousPilotGroup
  if s.previousPilotForce and game.forces[s.previousPilotForce] then player.force = s.previousPilotForce end
  s.pilotIndex, s.controlMode, s.generation = nil, "ai", s.generation + 1
end

function M.player_died(event)
  local s = Actor.state()
  if s.pilotIndex ~= event.player_index then return end
  local player = game.get_player(event.player_index)
  -- End native player respawn ownership; actor.lua owns the one replacement entity.
  player.set_controller{type = defines.controllers.spectator}
  player.permission_group = s.previousPilotGroup
  s.pilotIndex, s.controlMode = nil, "ai"
end

function M.ensure_attached()
  local s, c = Actor.state(), Actor.get()
  if not c or s.pilotIndex then return end
  for _, player in pairs(game.connected_players) do
    local ok, err = pcall(M.attach, player)
    if not ok then s.pilotError = U.error(err) end
    break
  end
  M.refresh()
end

return M
