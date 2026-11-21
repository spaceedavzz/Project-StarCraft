# Changelog

## Project StarCraft 1.0.0

The rework of KAESA StarCraft 1.0, released as a new mod. Internal names are unchanged, so crafts and saves keep working.

**New parts**
- Nosecone (Tanker), Nosecone (Cargo), Nosecone (Crewed)
  - Crewed: 10 crew, own IVA with working displays (ASET + RasterPropMonitor), EVA hatch, hinged docking cap over a
    0.9375 m docking well, FreeIva support
- Gigazilla catch tower: Base, Tower Section (clad and open), Tower Top, Carriage, Chopsticks
- Launch Mount (Pad 2 style) with retracting clamps
- Cargo Deployment Rail and Payload Adapter
- Globelink Solar Wing (large and small)

**Reworked**
- Forward flaps, aft flaps and their aero covers, rebuilt from public photos
- Grid fins: new model; own lattice aerodynamics (no longer based on the stock control surface); catch pins
- Booster Cluster rebuilt from photos (gimbal 15°); Booster chines; Raptor 3 class thrust (2850 kN at sea level) with the
  cluster scaled to the ship
- Hot gas RCS outlets are now flush bores in their pods instead of bell nozzles
- Ship Core / Booster Core / noses: much stiffer joints (the stack no longer bends at the ship/booster joint)
- Ship Core: symmetry when placing engines

**Fixes**
- Booster chine tops, stretched tile textures, dark band on the nose, tile UVs round the catch pins
- PEZ auto release no longer repeats "open the PEZ door" all through the launch
- Decouplers play the stock separation sound (and no longer log "Cannot find fx group")
- Zero-area triangles removed from the cluster, cargo rail and adapter
- All plugin messages in English

## KAESA StarCraft 1.0.0 (2026-11-17)

First public release.
