#include "../../native/HollowKnightTAS.ClockBridge/resident_audio_voice_control.h"
int main(void)
{
    hktas_vc_receipt receipt;
    hktas_vc_receipt_v2 receipt_v2;
    return hktas_vc_query(0,NULL,0,(BYTE *)&receipt,sizeof(receipt))
        +hktas_vc_query_v2(0,0,NULL,0,(BYTE *)&receipt_v2,sizeof(receipt_v2));
}
