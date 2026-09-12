local U = require("scripts.util")
local M = {}

-- Factorio 2.0.77 charges native handcraft inputs but omits production statistics
-- for a character with no player. Credit only outputs corroborated by queue
-- completion AND inventory. Connected players already receive native credit.
function M.prepare(c, recipe, requested, expected)
  local count, flows = requested, {}
  for _, ingredient in ipairs(recipe.ingredients) do
    U.check(ingredient.type == "item" and not expected[ingredient.name], "unsupported_craft",
      "Handcraft accounting requires solid, non-catalytic direct ingredients")
    count = math.min(count, math.floor(c.get_item_count(ingredient.name) / ingredient.amount))
  end
  U.check(count > 0, "direct_ingredients_missing", "Prepare direct ingredients before handcrafting; recursive queues are unsupported")
  for _, product in ipairs(recipe.products) do
    U.check(not product.extra_count_fraction or product.extra_count_fraction == 0, "unsupported_craft",
      "Random fractional handcraft outputs are unsupported")
    local amount = recipe.hidden_from_flow_stats and 0 or math.max(0, product.amount - (product.ignored_by_stats or 0))
    flows[product.name] = (flows[product.name] or 0) + amount
  end
  return {standalone = c.player == nil and c.associated_player == nil,
    recipe = recipe.name, flows = flows, creditedCrafts = 0, creditedProducts = {}}, count
end

function M.update(record, c)
  local w = record.work
  local a = w and w.accounting
  if not a or not w.queued or not c or not c.valid then return end
  local remaining = 0
  for _, entry in ipairs(c.crafting_queue or {}) do
    U.check(entry.recipe == a.recipe, "craft_queue_changed", "Unexpected recipe in the exclusive handcraft queue")
    remaining = remaining + entry.count
  end
  local completed = w.queued - remaining
  for name, amount in pairs(w.expected) do
    local observed = c.get_item_count(name) - (w.beforeInventory[name] or 0)
    completed = math.min(completed, math.floor(observed / amount))
  end
  U.check(completed >= a.creditedCrafts and completed <= w.queued, "craft_accounting_unconfirmed",
    "Native queue and inventory no longer corroborate completed crafts")
  local delta = completed - a.creditedCrafts
  if a.standalone and delta > 0 then
    local statistics = c.force.get_item_production_statistics(c.surface)
    for name, amount in pairs(a.flows) do
      if amount > 0 then
        statistics.on_flow(name, delta * amount)
        a.creditedProducts[name] = (a.creditedProducts[name] or 0) + delta * amount
      end
    end
  end
  a.creditedCrafts = completed
  record.receipt.effects.statistics = {source = a.standalone and "verified-playerless-output-credit" or "native-player",
    completedCrafts = completed, creditedProducts = U.copy(a.creditedProducts)}
end

return M
