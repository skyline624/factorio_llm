local U = require("scripts.util")
local Delivery = require("scripts.craft_delivery")
local M = {}

local function product_counter(c, name)
  local prototype = prototypes.item[name]
  if prototype.type ~= "ammo" then return c.get_main_inventory().get_item_count(name), 1, "actor-main-items" end
  local rounds = 0
  for index = 1, c.get_max_inventory_index() do
    local inv = c.get_inventory(index)
    if inv then
      for i = 1, #inv do
        local stack = inv[i]
        if stack.valid_for_read and stack.name == name and stack.quality.name == "normal" then
          rounds = rounds + (stack.count - 1) * prototype.magazine_size + stack.ammo
        end
      end
    end
  end
  return rounds, prototype.magazine_size, "actor-normal-ammunition-rounds"
end

function M.products(record, c)
  local w, products, evidence = record.work, {}, {}
  for name in pairs(w.expected) do
    local value, units, collection = product_counter(c, name)
    local before = w.accounting and w.accounting.beforeProducts and w.accounting.beforeProducts[name]
    U.check(before ~= nil, "craft_accounting_baseline_missing", "Craft output has no matching native baseline")
    products[name] = math.max(0, math.floor((value - before) / units))
    evidence[name] = {before = before, after = value, unitsPerItem = units, collection = collection}
  end
  record.receipt.effects.craftProductEvidence = evidence
  return products
end

-- Factorio 2.0.77 charges native handcraft inputs but omits production statistics
-- for a character with no player. Credit only outputs corroborated by queue
-- completion AND inventory. Connected players already receive native credit.
function M.prepare(c, recipe, requested, expected)
  local count, flows, before = requested, {}, {}
  for name in pairs(expected) do before[name] = product_counter(c, name) end
  for _, ingredient in ipairs(recipe.ingredients) do
    U.check(ingredient.type == "item" and not expected[ingredient.name], "unsupported_craft",
      "Handcraft accounting requires solid, non-catalytic direct ingredients")
    count = math.min(count, math.floor(c.get_main_inventory().get_item_count(ingredient.name) / ingredient.amount))
  end
  U.check(count > 0, "direct_ingredients_missing", "Prepare direct ingredients before handcrafting; recursive queues are unsupported")
  for _, product in ipairs(recipe.products) do
    U.check(not product.extra_count_fraction or product.extra_count_fraction == 0, "unsupported_craft",
      "Random fractional handcraft outputs are unsupported")
    local amount = recipe.hidden_from_flow_stats and 0 or math.max(0, product.amount - (product.ignored_by_stats or 0))
    flows[product.name] = (flows[product.name] or 0) + amount
  end
  return {standalone = c.player == nil and c.associated_player == nil,
    recipe = recipe.name, flows = flows, beforeProducts = before, creditedCrafts = 0, creditedProducts = {}}, count
end

function M.update(record, c)
  local w = record.work
  local a = w and w.accounting
  if not a or not w.queued or not c or not c.valid then return end
  Delivery.update(record, c)
  local remaining = 0
  for _, entry in ipairs(c.crafting_queue or {}) do
    U.check(entry.recipe == a.recipe, "craft_queue_changed", "Unexpected recipe in the exclusive handcraft queue")
    remaining = remaining + entry.count
  end
  local completed = w.queued - remaining
  local products = M.products(record, c)
  for name, amount in pairs(w.expected) do
    completed = math.min(completed, math.floor(products[name] / amount))
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
