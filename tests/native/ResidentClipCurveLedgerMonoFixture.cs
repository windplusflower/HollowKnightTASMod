using System;
using System.Runtime.CompilerServices;
public sealed class ClipSelf { public IntPtr Native; }
public static class ClipLedgerMonoFixture {
 [MethodImpl(MethodImplOptions.InternalCall)] private static extern void Mutate(ClipSelf self,string path,Type type,string attribute,object curve);
 public static int Run(IntPtr native,int mode) {
  try { Mutate(new ClipSelf {Native=native},new string('p',100),mode==1?null:typeof(ClipSelf),new string('a',100),new object()); return 10; }
  catch(InvalidOperationException) { return 42; }
 }
 public static void ThrowCallback() { throw new InvalidOperationException("caught native log callback"); }
}
