#pragma GCC diagnostic ignored "-Wunused-parameter"
#pragma GCC diagnostic ignored "-Wsign-compare"
#pragma GCC diagnostic ignored "-Wformat="
#pragma GCC diagnostic ignored "-Wparentheses"

#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>


#define DOV     0       // Verbose output for debugging

#ifdef DOHOST
unsigned int sleep(unsigned int seconds);
#else
//#include "synopsys_gpio.h"
//#include "ARMCM4_FP.h"
//#include "SEGGER_RTT.h"
#endif

void *memcpy(void *dest_str, const void *src_str, size_t number);

int __quantum__rt__qubit_allocate() { 
    //printf(">>> qubit_allocate()\n");
    return 0; 
}

#define aryMax 20
int* aryAdr[aryMax];    // Address of array
int  aryLen[aryMax];    // Allocated length
int  aryRef[aryMax];    // Need to keep track of ref count

#define aryBigMax 2
int  ary256[aryBigMax];      // Special casing the big one (just double buffer)
int  aryCnt  = 0;

int EXE_RESULT[32];

void aryInit() {
    for (int i=0; i<aryMax; i++) aryRef[i] = 0;
}

int* setAryLen(int len){
    for (int i=0; i<aryMax; i++) {
        if (aryRef[i]  == 0) {
            if (len == 256) {
                if (aryCnt < aryBigMax) {
                    if (DOV) printf("        >>> New 256 entry buffer %d at: %d\n",aryCnt,i);
                    aryLen[i]   = len;
                    aryAdr[i]   = (int*)malloc(len);
                    aryRef[i]   = 1;
                    ary256[aryCnt++] = i;
                }
                else {
                    i  = ary256[aryCnt++ % aryBigMax];
                    aryRef[i]   = 1;
                    if (DOV) printf("        >>> OLD 256 entry buffer (%d mod %d) at: %d\n",aryCnt-1,aryBigMax,i);
                }
            } else {
                aryLen[i] = len;
                aryAdr[i]   = (int*)malloc(len);
                aryRef[i] = 1;
                if (DOV) printf("    >>> setAryLen(%08x,%d) at %d/%d\n",aryAdr[i],len,i,1);
            }
            return aryAdr[i];
        }
    }
#ifdef DOHOST
    printf("!!!!!!!!!! SetAryLen: %08x,%d No room !!!!!!!!!!!!!\n",len);
#endif
    EXE_RESULT[0] = -2;
    exit(2);
}

int getAryLen(int* adr) {
    for (int i=0; i<aryMax; i++)
        if (aryRef[i] != 0 && aryAdr[i] == adr) {
            if (DOV) printf("    >>> getAryLen(%08x,%d) at %d/%d\n",adr,aryLen[i],i,aryRef[i]);
            return aryLen[i];
        }
#ifdef DOHOST
    printf("!!!!!!!!!! GetAryLen: %08x Not found !!!!!!!!!!!!!\n",adr);
#endif
    EXE_RESULT[0] = -1;
    exit(1);
}

void decAryRef(int* adr) {
    for (int i=0; i<aryMax; i++) {
        if (aryRef[i] != 0 && aryAdr[i] == (int*)adr) {
            if (aryLen[i] != 256) {
                if (--aryRef[i] == 0) free(adr);
                if (DOV) {
                    char* didFree = aryRef[i] == 0 ? " **FREED**" : "";
                    printf("    >>> decAryRef(%08x,%d) at %d/%d%s\n",adr,aryLen[i],i,aryRef[i],didFree);
                }
            } else if (DOV) printf("    >>> decAryRef(%08x,%d) at %d/%d IGNORED\n",adr,aryLen[i],i,aryRef[i]);
            return;
        }
    }
}

void incAryRef(int* adr) {
    for (int i=0; i<aryMax; i++) {
        if (aryRef[i] != 0 && aryAdr[i] == adr) {
            if (aryLen[i] != 256) {
                if (DOV) printf("    >>> incAryRef(%08x,%d) at %d/%d\n",adr,aryLen[i],i,aryRef[i]);
                aryRef[i]++;
            }
            return;
        }
    }
}

int* __quantum__rt__array_create_1d(int32_t arg1,int64_t arg2) {
    int len     = arg1*arg2;
    int* retVal =  setAryLen(len);
    if (DOV) printf(">>> %08x = array_create_1d(%d)\n",retVal,arg1);
    return retVal;
}
int* __quantum__rt__array_get_element_ptr_1d(int* arg1,int64_t arg2) {
    //printf(">>> %08x = array_get_element_ptr_1d(%08x,%d)\n",arg1+arg2,arg1,arg2);
    return (arg1+arg2);
}
int* __quantum__rt__array_copy(int* arg1) { 
    int len     = getAryLen(arg1);
    int* retVal = setAryLen(len);
    if (DOV) printf(">>> %08x = array_copy(%08x)\n",retVal,arg1);
    memcpy(retVal,arg1,len);
    return retVal;
}
int __quantum__qis__measure(int arg1) { 
    int bit = rand() & 0x800 == 0x800 ? 1 : 0; //@@@DBG int bit = 1;
    if (DOV) printf(">>> bit measured = %d\n",bit);
    return bit;
}

void __quantum__rt__array_unreference(int* arg1) { 
    decAryRef(arg1);
}

void __quantum__rt__array_reference(int* arg0) {
    incAryRef(arg0);
}

void __quantum__rt__qubit_release(int* arg1) { decAryRef(arg1); }
void __quantum__rt__result_unreference(int* arg1) { decAryRef(arg1); }

int __quantum__rt__result_equal(int arg1,int arg2) { 
    return arg1 == arg2;
}

int ResultOne = 1;
int __quantum__qis__cnot(int arg1) { return 0; }
int __quantum__qis__h(int arg1) { return 0; }
double __quantum__qis__intAsDouble(int arg1) { return (double)(arg1); }
int __quantum__qis__mz(int arg1) { return 0; }
int __quantum__qis__rx(int arg1) { return 0; }
int __quantum__qis__rz(int arg1) { return 0; }
int __quantum__qis__s(int arg1) { return 0; }
int __quantum__qis__z(int arg1) { return 0; }
int __quantum__qis__x(int arg1) { return 0; }
int __quantum__rt__tuple_create(int arg1) { return 0; }
int __quantum__rt__string_reference(int arg1) { return 0; }

extern int* Qrng_RandomInts();

int WinMain() { 
#ifndef DOHOST
    // initialize the JTAG printing library
    //SEGGER_RTT_Init();

    //GPIO0->SWPORTA_DR = 0;
    //GPIO0->SWPORTA_DDR = 0xFF;
#endif
    aryInit();  // Keeps track of lengths of allocated arrays

    // Main execution loop
    for (int loop=1; 1; loop++) {
        int* rslt = Qrng_RandomInts();
        EXE_RESULT[0] = loop;
        for (int i=1; i<32; i++) 
            EXE_RESULT[i] = rslt[i];

#ifdef DOHOST
        for (int i=0; i<32; i++)
            printf("%2d = %08x\n",i,EXE_RESULT[i]);
        sleep(1);
#else
        //GPIO0->SWPORTA_DR = 0x00;
        //GPIO0->SWPORTA_DR = 0xFF;
#endif
    }
return 0;
}

int main() { WinMain(); }