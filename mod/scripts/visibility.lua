local M = {}

-- Base 2.0.77 player coverage: a 5x5 chunk square around the occupied chunk.
-- A standalone LuaEntity does not perform the player's chart updates. Factorio
-- can defer charting for a force with no players, so local sight is also bounded
-- explicitly to the normal character square. No past enemy position grants sight.
function M.is_visible(character, entity)
  if entity.surface ~= character.surface then return false end
  local cx, cy = math.floor(character.position.x / 32), math.floor(character.position.y / 32)
  local ex, ey = math.floor(entity.position.x / 32), math.floor(entity.position.y / 32)
  if math.abs(ex - cx) <= 2 and math.abs(ey - cy) <= 2 then return true end
  return character.force.is_chunk_visible(entity.surface, {x = ex, y = ey})
end

-- Request only normal exploration chart data for a future connected pilot.
-- This request is not itself evidence that a chunk is currently visible.
function M.refresh(character)
  local x, y = math.floor(character.position.x / 32), math.floor(character.position.y / 32)
  -- A standalone character does not request the player's surrounding map
  -- generation. Charting alone can leave terrain-only chunks and out-of-map
  -- edges indefinitely. Let the native generator finish the normal local
  -- 5x5 square asynchronously; never generate a remote destination or flush
  -- unrelated outstanding requests with force_generate_chunk_requests.
  character.surface.request_to_generate_chunks(character.position, 2)
  character.force.chart(character.surface, {{(x - 2) * 32, (y - 2) * 32},
    {(x + 3) * 32 - 0.03125, (y + 3) * 32 - 0.03125}})
end

return M
