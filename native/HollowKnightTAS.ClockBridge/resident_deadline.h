#ifndef HKTAS_RESIDENT_DEADLINE_H
#define HKTAS_RESIDENT_DEADLINE_H
/* Restore remains a 9s transaction. Capture/observation may inspect a large
 * graph without retiring the source before its packet is complete. */
#define HKTAS_RESIDENT_RESTORE_WALL 360000u
#define HKTAS_RESIDENT_SOURCE_WALL 240000u
static unsigned long g_resident_wall_limit = HKTAS_RESIDENT_RESTORE_WALL;
#endif
