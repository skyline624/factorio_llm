local U = require("scripts.util")
local Actor = require("scripts.actor")
local Actions = require("scripts.actions")
local M = {capacity = 2048}

local function record_for(id)
  local record = Actor.state().receipts[U.string(id, "operationId")]
  U.check(record ~= nil, "operation_unknown", "Receipt absent or expired; reconcile before retrying")
  return record
end

function M.get(id) return U.copy(record_for(id).receipt) end

function M.active()
  local s = Actor.state()
  if s.activeId then return s.receipts[s.activeId] end
end

function M.finish(record, status, err)
  local stop_ok, stop_err = pcall(Actor.stop, record.request.kind == "craft")
  if not stop_ok then
    record.receipt.stopError = U.error(stop_err)
    Actor.state().stopUnconfirmed = true
    status, err = "failed", {code = "stop_unconfirmed", message = "Native stop failed; new operations are blocked"}
  end
  local measure_ok, measure_err = pcall(Actions.measure, record, Actor.get())
  if not measure_ok then record.receipt.measurementError = U.error(measure_err) end
  record.receipt.status, record.receipt.updatedTick = status, game.tick
  if err then record.receipt.error = U.error(err) end
  if Actor.state().activeId == record.receipt.operationId then Actor.state().activeId = nil end
  Actor.state().lastOperationId = record.receipt.operationId
end

function M.cancel(id, reason)
  local record = record_for(id)
  if record.receipt.status == "running" or record.receipt.status == "accepted" then
    M.finish(record, "cancelled", {code = "cancelled", message = reason or "Cancelled by controller"})
  end
  return U.copy(record.receipt)
end

function M.cancel_active(reason)
  local record = M.active()
  if record then M.cancel(record.receipt.operationId, reason) end
end

local function preconditions(request, c)
  U.check(not Actor.state().awaitingController, "controller_reconciliation_required", "Resume the prepared checkpoint before submitting work")
  U.check(U.canonical(request.scope) == U.canonical(Actor.scope()), "stale_scope", "Actor scope has changed")
  U.check(Actor.state().controlMode == "ai", "manual_control", "A human pilot owns the actor")
  U.check(not Actor.state().stopUnconfirmed, "stop_unconfirmed", "Previous native action has not been confirmed stopped")
  local conditions = request.preconditions or {}
  U.check(type(conditions) == "table", "invalid_preconditions", "Preconditions must be an object")
  if conditions.inventory then
    U.check(type(conditions.inventory) == "table", "invalid_preconditions", "inventory must be an object")
    for name, minimum in pairs(conditions.inventory) do
      U.string(name, "inventory item")
      minimum = U.number(minimum, "minimum inventory", 0, 1000000, nil, true)
      U.check(c.get_main_inventory().get_item_count(name) >= minimum,
        "inventory_precondition", "Insufficient current inventory: " .. name)
    end
  end
  if conditions.position then
    local tolerance = U.number(conditions.positionTolerance, "positionTolerance", 0.1, 10, 0.5)
    U.check(U.distance(c.position, U.position(conditions.position)) <= tolerance,
      "position_precondition", "The character is no longer at the expected position")
  end
end

function M.submit(request)
  U.check(type(request) == "table", "invalid_arguments", "submit arguments must be an object")
  local id = U.string(request.operationId, "operationId")
  U.string(request.fingerprint, "fingerprint")
  U.string(request.kind, "kind")
  U.check(type(request.args) == "table" and type(request.scope) == "table", "invalid_arguments", "args and scope are required objects")
  local canonical = U.canonical(request)
  local s = Actor.state()
  local previous = s.receipts[id]
  if previous then
    U.check(previous.canonical == canonical, "operation_conflict", "This operationId already denotes different content")
    return U.copy(previous.receipt)
  end
  local record = {request = U.copy(request), canonical = canonical,
    receipt = {operationId = id, kind = request.kind, status = "accepted",
      acceptedTick = game.tick, updatedTick = game.tick, effects = {}}}
  s.receipts[id] = record
  s.receiptOrder[#s.receiptOrder + 1] = id
  if #s.receiptOrder > M.capacity then
    local index = s.receiptOrder[1] == s.activeId and 2 or 1
    local expired = table.remove(s.receiptOrder, index)
    s.receipts[expired] = nil
  end
  local accepted, rejection = pcall(function()
    U.check(s.worldId and s.sessionId, "handshake_required", "Call hello first")
    U.check(s.activeId == nil, "actor_busy", "An operation already owns the actor")
    U.number(request.deadlineTick, "deadlineTick", game.tick + 1, game.tick + 216000, nil, true)
    local c = Actor.get()
    U.check(c ~= nil, "actor_dead", "The actor is waiting to respawn")
    preconditions(request, c)
  end)
  if not accepted then
    record.receipt.status, record.receipt.error = "rejected", U.error(rejection)
    return U.copy(record.receipt)
  end
  s.activeId = id
  record.receipt.status = "running"
  local ok, result = pcall(Actions.start, record, Actor.get())
  if not ok then M.finish(record, "failed", result)
  elseif result then M.finish(record, result)
  else Actions.measure(record, Actor.get()) end
  return U.copy(record.receipt)
end

function M.tick()
  local state = Actor.state()
  if state.stopUnconfirmed then
    local ok = pcall(Actor.stop, true)
    if ok then state.stopUnconfirmed, state.generation = nil, state.generation + 1 end
    return
  end
  local record = M.active()
  if not record then return end
  local c = Actor.get()
  if not c then
    M.finish(record, "failed", {code = "actor_dead", message = "Actor died during operation"})
    return
  end
  if game.tick >= record.request.deadlineTick then
    M.finish(record, "failed", {code = "deadline_exceeded", message = "The operation exhausted its tick budget"})
    return
  end
  local ok, result = pcall(Actions.step, record, c)
  if not ok then M.finish(record, "failed", result)
  elseif result then M.finish(record, result)
  else
    record.receipt.updatedTick = game.tick
    -- Avoid allocating inventory tables on every walking/waiting tick.
    if game.tick % 30 == 0 then Actions.measure(record, c) end
  end
end

function M.last_receipt()
  local s = Actor.state()
  local id = s.activeId or s.lastOperationId
  if id and s.receipts[id] then return U.copy(s.receipts[id].receipt) end
end

return M
