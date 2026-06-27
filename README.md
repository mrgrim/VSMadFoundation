## Yet Another Core/Lib Mod?

Ya, I'm afraid so. I have a few in flight mods, and some needed the same harmony patches. So it made the most sense to break all that shared stuff out and put it here. Don't run off yet, though! It does contain a couple of vanilla bug fixes you might find interesting. Also, if you're a modder, it extends vanilla capabilities in ways you might be able to use.

### Vanilla Extensions

 - Multiple `GroundStoredProcessable` behaviors on an item now work as you would expect! Want one interaction for shears, one for a knife, and one for a hammer? It will work. Block interaction tips and handbook entries should "just work".
 - Transition state info is communicated on ground stored items. Rotting, curing, drying all show in the block info overlay.

### Vanilla Bug Fixes

- If an interaction on a block causes the block to be destroyed you could end up with several issues. Most common are stuck animations or sound effects. The cause is that sometimes this could prevent the "stop" code for the interaction from ever being called on the client. That's fixed. The stop code will always be called now.
- "Messy12" ground storages (the kind where a messy pile of 12 things get placed, think ores, for example) that somehow ended up with more than 12 items would render the extra items all piled in the same place rotated around each other. This not only looked weird with lots of flickering and blurriness, it could become a big performance problem if the overflow was large. Not a common scenario, but my mod Mad Hides allows you to stack 32 huge hides in a pile, which perish into 256 rot, which is _way more_ than 12! So I needed this fix.
- If you use Food Shelves you probably ran into this next one. "Multiblock" blocks, the blocks for large things like the 2 wide shelf, do not apply the property that prioritized interaction with the block instead of what's in your hand. This is what allows you to right click and interact with the block while holding food instead of trying to eat. That's fixed.
