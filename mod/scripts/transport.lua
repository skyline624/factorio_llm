local U = require("scripts.util")
local M = {}

-- Native transport facts only. Each caller supplies its ordinary visibility/known-own-entity predicate.
function M.read(entity, known)
  local value = {}
  local function read_red(into)
    local connector = entity.get_wire_connector(defines.wire_connector_id.circuit_red, false)
    into.redNeighbours, into.redNeighbourCount = {}, connector and #connector.connections or 0
    if connector then
      for _, connection in pairs(connector.connections) do
        local owner = connection.target.owner
        if owner and owner.valid and known(owner) then into.redNeighbours[#into.redNeighbours + 1] = U.entity_id(owner) end
      end
      table.sort(into.redNeighbours)
    end
  end
  if entity.type == "inserter" then
    value.pickupPosition, value.dropPosition = U.copy(entity.pickup_position), U.copy(entity.drop_position)
    for _, side in ipairs{"pickup", "drop"} do
      local target = entity[side .. "_target"]
      if target and target.valid and known(target) then value[side .. "TargetId"] = U.entity_id(target) end
    end
    local filters, normal = {}, true
    for slot = 1, entity.filter_slot_count do
      local filter = entity.get_filter(slot)
      if filter then
        filters[#filters + 1] = type(filter) == "string" and filter or filter.name
        if type(filter) ~= "string" then
          local quality = filter.quality
          normal = normal and (quality == nil or quality == "normal" or type(quality) ~= "string" and quality.name == "normal")
            and (filter.comparator == nil or filter.comparator == "=")
        end
      end
    end
    local control = entity.get_control_behavior()
    local condition = control and control.circuit_condition
    value.inserterControl = {useFilters = entity.use_filters, filterMode = entity.inserter_filter_mode, filters = filters,
      circuitEnabled = control and control.circuit_enable_disable or false,
      circuitItem = condition and condition.first_signal and condition.first_signal.name,
      comparator = condition and condition.comparator, maximum = condition and condition.constant,
      nativeNormalFilters = normal, circuitSetsFilters = control and control.circuit_set_filters or false,
      circuitReadsHand = control and control.circuit_read_hand_contents or false,
      logisticCondition = control and control.connect_to_logistic_network or false, scriptDisabled = entity.disabled_by_script}
    read_red(value.inserterControl)
  elseif entity.type == "transport-belt" or entity.type == "underground-belt" or entity.type == "splitter" then
    value.beltConnections = {inputs = {}, outputs = {}}
    local neighbours = entity.belt_neighbours
    for _, side in ipairs{"inputs", "outputs"} do
      value.beltConnections[side .. "Count"] = #neighbours[side]
      for _, target in pairs(neighbours[side]) do
        if target.valid and known(target) then value.beltConnections[side][#value.beltConnections[side] + 1] = U.entity_id(target) end
      end
      table.sort(value.beltConnections[side])
    end
  elseif entity.type == "container" then read_red(value)
  else return nil end
  return value
end

return M
