# LadderLover

ExileAPI plugin that labels uniques on the ground based on whether you own them, using [poeladder.com](https://poeladder.com) collection data.

## What it does

Shows a label over dropped uniques so you can tell at a glance if you still need them for your collection. By default, uniques you do not own get a label and ones you do own are hidden. You can toggle showing owned items too for testing.

Works with both identified and unidentified uniques. Identified items are matched by name. Unidentified items are matched by their art asset path using a static mapping file, since the ExileCore offset for reading unique descriptions from memory is currently stale and returns nothing.

## Setup

1. You must have a poeladder.com account. Sign up there first.
2. Enter your poeladder username in the config. This is your PathofExile.com username with the `#` replaced by a `-`. For example `User#1234` becomes `User-1234`.
3. Hit Save. A league dropdown will appear. Pick your curio league.
4. The plugin fetches your unowned unique list from poeladder and caches it locally for 2 hours.

## How ownership works

The poeladder filters endpoint returns a list of uniques that are not yet in your collection. Anything in that list is shown as not owned. Anything not in the list is assumed owned. The cache refreshes every 2 hours, so a unique you just picked up may still show as not owned until the next refresh.

## Art path data

The embedded `uniqueArtMapping.default.json` comes from [Get-Chaos-Value](https://github.com/exApiTools/Get-Chaos-Value). Without it, unidentified uniques cannot be resolved since the ExileCore `UniqueItemDescriptions` file offset is stale and reads back empty on current PoE. If new uniques are added to the game, this file will need updating until that offset is fixed.

## Disclaimer

This plugin is not affiliated with or endorsed by Grinding Gear Games or poeladder.com. Use at your own risk. I am not responsible for any consequences of using this plugin.