using Neo.VM;
using System.Collections.Generic;

namespace Neo.SmartContract
{
    partial class ApplicationEngine
    {
        public static readonly IReadOnlyDictionary<OpCode, long> OpCodePrices = new Dictionary<OpCode, long>
        {
            [OpCode.PUSHINT8] = 30,
            [OpCode.PUSHINT16] = 30,
            [OpCode.PUSHINT32] = 30,
            [OpCode.PUSHINT64] = 30,
            [OpCode.PUSHINT128] = 120,
            [OpCode.PUSHINT256] = 120,
            [OpCode.PUSHA] = 120,
            [OpCode.PUSHNULL] = 30,
            [OpCode.PUSHDATA1] = 180,
            [OpCode.PUSHDATA2] = 13000,
            [OpCode.PUSHDATA4] = 110000,
            [OpCode.PUSHM1] = 30,
            [OpCode.PUSH0] = 30,
            [OpCode.PUSH1] = 30,
            [OpCode.PUSH2] = 30,
            [OpCode.PUSH3] = 30,
            [OpCode.PUSH4] = 30,
            [OpCode.PUSH5] = 30,
            [OpCode.PUSH6] = 30,
            [OpCode.PUSH7] = 30,
            [OpCode.PUSH8] = 30,
            [OpCode.PUSH9] = 30,
            [OpCode.PUSH10] = 30,
            [OpCode.PUSH11] = 30,
            [OpCode.PUSH12] = 30,
            [OpCode.PUSH13] = 30,
            [OpCode.PUSH14] = 30,
            [OpCode.PUSH15] = 30,
            [OpCode.PUSH16] = 30,
            [OpCode.NOP] = 30,
            [OpCode.JMP] = 70,
            [OpCode.JMP_L] = 70,
            [OpCode.JMPIF] = 70,
            [OpCode.JMPIF_L] = 70,
            [OpCode.JMPIFNOT] = 70,
            [OpCode.JMPIFNOT_L] = 70,
            [OpCode.JMPEQ] = 70,
            [OpCode.JMPEQ_L] = 70,
            [OpCode.JMPNE] = 70,
            [OpCode.JMPNE_L] = 70,
            [OpCode.JMPGT] = 70,
            [OpCode.JMPGT_L] = 70,
            [OpCode.JMPGE] = 70,
            [OpCode.JMPGE_L] = 70,
            [OpCode.JMPLT] = 70,
            [OpCode.JMPLT_L] = 70,
            [OpCode.JMPLE] = 70,
            [OpCode.JMPLE_L] = 70,
            [OpCode.CALL] = 22000,
            [OpCode.CALL_L] = 22000,
            [OpCode.CALLA] = 22000,
            [OpCode.ABORT] = 30,
            [OpCode.ASSERT] = 30,
            [OpCode.THROW] = 22000,
            [OpCode.TRY] = 100,
            [OpCode.TRY_L] = 100,
            [OpCode.ENDTRY] = 100,
            [OpCode.ENDTRY_L] = 100,
            [OpCode.ENDFINALLY] = 100,
            [OpCode.RET] = 0,
            [OpCode.SYSCALL] = 0,
            [OpCode.DEPTH] = 60,
            [OpCode.DROP] = 60,
            [OpCode.NIP] = 60,
            [OpCode.XDROP] = 400,
            [OpCode.CLEAR] = 400,
            [OpCode.DUP] = 60,
            [OpCode.OVER] = 60,
            [OpCode.PICK] = 60,
            [OpCode.TUCK] = 60,
            [OpCode.SWAP] = 60,
            [OpCode.ROT] = 60,
            [OpCode.ROLL] = 400,
            [OpCode.REVERSE3] = 60,
            [OpCode.REVERSE4] = 60,
            [OpCode.REVERSEN] = 400,
            [OpCode.INITSSLOT] = 400,
            [OpCode.INITSLOT] = 800,
            [OpCode.LDSFLD0] = 60,
            [OpCode.LDSFLD1] = 60,
            [OpCode.LDSFLD2] = 60,
            [OpCode.LDSFLD3] = 60,
            [OpCode.LDSFLD4] = 60,
            [OpCode.LDSFLD5] = 60,
            [OpCode.LDSFLD6] = 60,
            [OpCode.LDSFLD] = 60,
            [OpCode.STSFLD0] = 60,
            [OpCode.STSFLD1] = 60,
            [OpCode.STSFLD2] = 60,
            [OpCode.STSFLD3] = 60,
            [OpCode.STSFLD4] = 60,
            [OpCode.STSFLD5] = 60,
            [OpCode.STSFLD6] = 60,
            [OpCode.STSFLD] = 60,
            [OpCode.LDLOC0] = 60,
            [OpCode.LDLOC1] = 60,
            [OpCode.LDLOC2] = 60,
            [OpCode.LDLOC3] = 60,
            [OpCode.LDLOC4] = 60,
            [OpCode.LDLOC5] = 60,
            [OpCode.LDLOC6] = 60,
            [OpCode.LDLOC] = 60,
            [OpCode.STLOC0] = 60,
            [OpCode.STLOC1] = 60,
            [OpCode.STLOC2] = 60,
            [OpCode.STLOC3] = 60,
            [OpCode.STLOC4] = 60,
            [OpCode.STLOC5] = 60,
            [OpCode.STLOC6] = 60,
            [OpCode.STLOC] = 60,
            [OpCode.LDARG0] = 60,
            [OpCode.LDARG1] = 60,
            [OpCode.LDARG2] = 60,
            [OpCode.LDARG3] = 60,
            [OpCode.LDARG4] = 60,
            [OpCode.LDARG5] = 60,
            [OpCode.LDARG6] = 60,
            [OpCode.LDARG] = 60,
            [OpCode.STARG0] = 60,
            [OpCode.STARG1] = 60,
            [OpCode.STARG2] = 60,
            [OpCode.STARG3] = 60,
            [OpCode.STARG4] = 60,
            [OpCode.STARG5] = 60,
            [OpCode.STARG6] = 60,
            [OpCode.STARG] = 60,
            [OpCode.NEWBUFFER] = 80000,
            [OpCode.MEMCPY] = 80000,
            [OpCode.CAT] = 80000,
            [OpCode.SUBSTR] = 80000,
            [OpCode.LEFT] = 80000,
            [OpCode.RIGHT] = 80000,
            [OpCode.INVERT] = 100,
            [OpCode.AND] = 200,
            [OpCode.OR] = 200,
            [OpCode.XOR] = 200,
            [OpCode.EQUAL] = 200,
            [OpCode.NOTEQUAL] = 200,
            [OpCode.SIGN] = 100,
            [OpCode.ABS] = 100,
            [OpCode.NEGATE] = 100,
            [OpCode.INC] = 100,
            [OpCode.DEC] = 100,
            [OpCode.ADD] = 200,
            [OpCode.SUB] = 200,
            [OpCode.MUL] = 300,
            [OpCode.DIV] = 300,
            [OpCode.MOD] = 300,
            [OpCode.SHL] = 300,
            [OpCode.SHR] = 300,
            [OpCode.NOT] = 100,
            [OpCode.BOOLAND] = 200,
            [OpCode.BOOLOR] = 200,
            [OpCode.NZ] = 100,
            [OpCode.NUMEQUAL] = 200,
            [OpCode.NUMNOTEQUAL] = 200,
            [OpCode.LT] = 200,
            [OpCode.LE] = 200,
            [OpCode.GT] = 200,
            [OpCode.GE] = 200,
            [OpCode.MIN] = 200,
            [OpCode.MAX] = 200,
            [OpCode.WITHIN] = 200,
            [OpCode.PACK] = 7000,
            [OpCode.UNPACK] = 7000,
            [OpCode.NEWARRAY0] = 400,
            [OpCode.NEWARRAY] = 15000,
            [OpCode.NEWARRAY_T] = 15000,
            [OpCode.NEWSTRUCT0] = 400,
            [OpCode.NEWSTRUCT] = 15000,
            [OpCode.NEWMAP] = 200,
            [OpCode.SIZE] = 150,
            [OpCode.HASKEY] = 270000,
            [OpCode.KEYS] = 500,
            [OpCode.VALUES] = 7000,
            [OpCode.PICKITEM] = 270000,
            [OpCode.APPEND] = 15000,
            [OpCode.SETITEM] = 270000,
            [OpCode.REVERSEITEMS] = 500,
            [OpCode.REMOVE] = 500,
            [OpCode.CLEARITEMS] = 400,
            [OpCode.ISNULL] = 60,
            [OpCode.ISTYPE] = 60,
            [OpCode.CONVERT] = 80000,
        };
    }
}
