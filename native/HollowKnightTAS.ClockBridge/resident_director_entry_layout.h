#ifndef HKTAS_DIRECTOR_ENTRY_LAYOUT_H
#define HKTAS_DIRECTOR_ENTRY_LAYOUT_H
/* Fixed PE d97e92a7, RSDS 0334be90-63dd-4644-bee1-fc8d56c840c2/1.
 * Each row records a whole instruction prefix with no position-sensitive
 * operands and its truncated x64 unwind program. */
#define HKTAS_DE_ENTRIES(X) \
 X(manager_ScheduleGraphCommand, VIP, 0x899db0u, 115u, UINT64_C(0x4a166218dc714b23), 5u, "72,137,92,36,8", "1,16,0,0") \
 X(manager_ScheduleGraphDestroy, VP, 0x899e30u, 116u, UINT64_C(0x6b116a11b0f47aa3), 5u, "72,137,92,36,8", "1,16,0,0") \
 X(manager_ConstructPlayableGraph, PP, 0x8967e0u, 164u, UINT64_C(0x80aff438c7ed057f), 5u, "72,137,92,36,8", "1,16,0,0") \
 X(manager_ProcessAndClearForShutdown, V0, 0x899120u, 238u, UINT64_C(0x226a93b06e4f6fa5), 5u, "72,137,92,36,8", "1,16,0,0") \
 X(manager_ProcessPlayStateChanges, V0, 0x899210u, 1660u, UINT64_C(0xad546f813b63053e), 7u, "72,139,196,72,137,72,8", "1,18,0,0") \
 X(manager_QueueAndEvaluateGraph, VP, 0x899890u, 81u, UINT64_C(0x229c864bdb74a16b), 6u, "64,83,72,131,236,32", "1,17,2,0,6,50,2,48") \
 X(manager_ExecutePrepareFrames, VI, 0x8977f0u, 768u, UINT64_C(0x8281cf03129fef16), 5u, "72,137,92,36,16", "1,16,0,0") \
 X(manager_ExecuteStage, VI, 0x897af0u, 379u, UINT64_C(0x43ff4762587174ce), 5u, "72,137,92,36,16", "1,16,0,0") \
 X(manager_RegisterProcessJobs, VP, 0x899b30u, 585u, UINT64_C(0xd67d758a40967d6e), 11u, "64,83,65,85,72,129,236,152,0,0,0", "1,22,4,0,11,1,19,0,4,208,2,48") \
 X(manager_RebuildPrepareFrameJobs, V0, 0x8998f0u, 569u, UINT64_C(0x62c1ac6982834fc0), 11u, "72,139,196,85,72,129,236,144,0,0,0", "1,22,3,0,11,1,18,0,4,80") \
 X(manager_BumpFrameIDs, VI, 0x8960c0u, 170u, UINT64_C(0xbb6b8225bd08cc5c), 5u, "72,137,92,36,8", "1,16,0,0") \
 X(manager_AcquireNode, P0, 0x895f30u, 84u, UINT64_C(0x0c38834fb16c5c49), 11u, "72,131,236,56,72,139,137,64,4,0,0", "1,22,1,0,4,98") \
 X(manager_AcquireOutputNode, P0, 0x895f90u, 110u, UINT64_C(0x420223e234177da1), 6u, "64,83,72,131,236,48", "1,17,2,0,6,82,2,48") \
 X(manager_FreeNode, VP, 0x898290u, 24u, UINT64_C(0x223afeb6c0c73248), 12u, "131,66,32,2,72,199,66,40,0,0,0,0", "1,23,0,0") \
 X(manager_FreeOutputNode, VP, 0x8982b0u, 46u, UINT64_C(0xdc55d9f80945b2a8), 6u, "64,83,72,131,236,32", "1,17,2,0,6,50,2,48") \
 X(manager_CacheScriptingMethodsForClass, SP, 0x896170u, 795u, UINT64_C(0x6e8918cdce2c7dd7), 6u, "64,85,86,87,65,86", "1,17,4,0,6,224,4,112,3,96,2,80") \
 X(manager_SetApplicationPause, VB, 0x89a230u, 200u, UINT64_C(0xee75033b34023253), 6u, "65,87,72,131,236,48", "1,17,2,0,6,82,2,240") \
 X(manager_ResetFrameTiming, V0, 0x899d80u, 35u, UINT64_C(0x91d095e149e44e79), 6u, "64,83,72,131,236,32", "1,17,2,0,6,50,2,48") \
 X(manager_EvaluateGraph, VPD, 0x8977b0u, 56u, UINT64_C(0x5cee8db7f0d98992), 6u, "76,139,2,77,133,192", "1,17,0,0") \
 X(pool_AllocateInBucket, PI, 0x896060u, 96u, UINT64_C(0xfaf2bf6a78cef84a), 5u, "72,137,92,36,8", "1,16,0,0") \
 X(pool_CreateItem, PI, 0x896980u, 263u, UINT64_C(0x16bd4d8ead40d702), 5u, "72,137,92,36,8", "1,16,0,0") \
 X(pool_Free, VP, 0x898270u, 26u, UINT64_C(0xb1093ce00f6fe9e0), 7u, "72,99,66,96,76,139,193", "1,18,0,0") \
 X(pool_SwitchBucket, PP, 0x89a850u, 381u, UINT64_C(0x1436321ac12eb416), 8u, "64,83,85,86,72,131,236,32", "1,19,4,0,8,50,4,96,3,80,2,48") \
 X(pool_Destruct, V0, 0x896f30u, 197u, UINT64_C(0x1f544e1a3074a86b), 5u, "72,137,92,36,8", "1,16,0,0") \
 X(graph_Play, V0, 0x898de0u, 294u, UINT64_C(0x0f6a0dff74c5f5fc), 6u, "64,83,72,131,236,64", "1,17,2,0,6,114,2,48") \
 X(graph_Stop, V0, 0x89a780u, 197u, UINT64_C(0x3b10464dc824fe7c), 6u, "64,83,72,131,236,64", "1,17,2,0,6,114,2,48") \
 X(graph_DestroyPendingPlayables, V0, 0x896b00u, 459u, UINT64_C(0x178117d4db23203c), 5u, "72,137,92,36,8", "1,16,0,0") \
 X(graph_DestroyPlayableDeferred, VP, 0x896cd0u, 194u, UINT64_C(0x0df58b64edd6440b), 5u, "72,137,92,36,8", "1,16,0,0") \
 X(graph_DestroySubGraphDeferred, VP, 0x896e40u, 194u, UINT64_C(0xd3527b912be27821), 5u, "72,137,92,36,8", "1,16,0,0") \
 X(graph_DisableMultithread, V0, 0x897000u, 220u, UINT64_C(0xf5bb7773f12c57ed), 6u, "64,83,72,131,236,64", "1,17,2,0,6,114,2,48") \
 X(graph_SetTimeUpdateMode, VI, 0x89a580u, 231u, UINT64_C(0xf39123802b4f84ca), 5u, "72,137,92,36,16", "1,16,0,0") \
 X(graph_EvaluateDeferred, V0, 0x897670u, 308u, UINT64_C(0x090b7ff42703ec5e), 6u, "64,83,72,131,236,64", "1,17,2,0,6,114,2,48") \
 X(graph_DispatchOutputNotifications, V0, 0x8970e0u, 385u, UINT64_C(0x0e019f867f604b4d), 5u, "72,139,196,65,87", "1,16,1,0,5,240") \
 X(graph_PrepareFrame, VPREP, 0x898f10u, 373u, UINT64_C(0x0709d568e7540d11), 5u, "72,139,196,85,86", "1,16,2,0,5,96,4,80") \
 X(graph_Evaluate, VEVAL, 0x8972d0u, 913u, UINT64_C(0x15c6942d19eb640e), 5u, "72,137,76,36,8", "1,16,0,0") \
 X(graph_FireConnectionHashChanged, V0, 0x897c70u, 124u, UINT64_C(0x2dd25e2452198fcc), 5u, "72,137,92,36,16", "1,16,0,0") \
 X(graph_DestroyOutput, VP, 0x896a90u, 111u, UINT64_C(0x3177fab518dd1edc), 6u, "64,83,72,131,236,32", "1,17,2,0,6,50,2,48") \
 X(graph_UpdateRootPlayableCache, V0, 0x89ab70u, 374u, UINT64_C(0x102413bd6e81b4d1), 6u, "64,85,72,131,236,64", "1,17,2,0,6,114,2,80") \
 X(graph_SchedulePlayableDestruction, VP, 0x899eb0u, 158u, UINT64_C(0xad277748debef09a), 5u, "72,137,92,36,16", "1,16,0,0") \
 X(graph_ScheduleSubgraphDestruction, VP, 0x899f50u, 178u, UINT64_C(0x2ef1e69d6c7de70d), 5u, "72,137,116,36,16", "1,16,0,0") \
 X(graph_SetHasPendingNotifications, V0, 0x89a300u, 274u, UINT64_C(0x30ce2f4127b065b9), 6u, "64,83,72,131,236,64", "1,17,2,0,6,114,2,48") \
 X(graph_InvalidateMultithread, V0, 0x898c40u, 190u, UINT64_C(0xac6bf490c4c1d17a), 6u, "64,83,72,131,236,64", "1,17,2,0,6,114,2,48") \
 X(hgraph_Create, PP, 0x896890u, 227u, UINT64_C(0x89e5aed4f4b66e83), 5u, "72,137,92,36,8", "1,16,0,0") \
 X(graph_Handle, PP, 0x898780u, 163u, UINT64_C(0x3a519a9cbfa605b4), 5u, "72,137,92,36,16", "1,16,0,0") \
 X(job_PrepareFrame, JOB, 0x899090u, 131u, UINT64_C(0x64835f4edec19cee), 6u, "72,131,236,56,139,194", "1,17,1,0,4,98") \
 X(job_Schedule, SCHED, 0x725da0u, 176u, UINT64_C(0x9908abea8207f7fd), 5u, "72,137,92,36,8", "1,16,0,0") \
 X(job_Complete, FENCE, 0x725510u, 63u, UINT64_C(0xbf113a787aa2ec1e), 5u, "72,137,92,36,8", "1,16,0,0") \
 X(job_Group, GROUP, 0x727780u, 349u, UINT64_C(0x903041bea2a0bcd9), 5u, "72,137,92,36,24", "1,16,0,0") \
 X(job_Exec, EXEC, 0x726de0u, 275u, UINT64_C(0x65f5869a0a8451c9), 5u, "72,137,92,36,8", "1,16,0,0")
#endif
