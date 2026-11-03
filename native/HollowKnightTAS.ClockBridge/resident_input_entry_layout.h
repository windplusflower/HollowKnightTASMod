#ifndef HKTAS_RESIDENT_INPUT_ENTRY_LAYOUT_H
#define HKTAS_RESIDENT_INPUT_ENTRY_LAYOUT_H
/* Fixed PE d97e92a7; PDB 0334be90-63dd-4644-bee1-fc8d56c840c2/1.
 * Layout only: no runtime installation or platform-input qualification. */
#define HKTAS_INPUT_ENTRIES(X) \
 X(QueueInputEvent, BP, 0x292a30u, 574u, UINT64_C(0xeb7a32d8c33965c4), 5u, "64,85,87,65,84", "1,16,3,0,5,192,3,112,2,80") \
 X(SendInputEventsToScript, VI, 0x293690u, 518u, UINT64_C(0x5ce3c7ce9e1a6f72), 5u, "137,76,36,8,83", "1,16,1,0,5,48")
#endif
