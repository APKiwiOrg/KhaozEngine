namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Real FXC output for the reader's facts (<see cref="TemporalVisibilityDxbcTests"/>): the <c>ps_5_0</c> instruction
    /// listings of the visibility probe, D's function alone, that hosted diagnostic run 39216044678 (job 117940080797,
    /// commit d1f76b689) printed, copied verbatim from <c>ps_5_0</c> to <c>ret</c> with the header comments and trailing
    /// spaces left out. At the shipped flags, 0x8000, FXC reassociated D's chain to <c>(at - J) + 0.5</c>. Under IEEE
    /// strictness, 0xA000, it kept the written <c>(at + 0.5) - J</c> and marked every instruction precise.
    /// </summary>
    internal static class TemporalVisibilityDxbcSamples
    {
        /// <summary>The probe at the shipped flags, 0x8000.</summary>
        internal const string ProbeShipped = @"
ps_5_0
dcl_globalFlags refactoringAllowed
dcl_constantbuffer CB0[7], immediateIndexed
dcl_resource_texture2d (float,float,float,float) t0
dcl_resource_texture2d (float,float,float,float) t1
dcl_resource_texture2d (float,float,float,float) t2
dcl_resource_texture2d (float,float,float,float) t3
dcl_resource_texture2d (float,float,float,float) t4
dcl_input_ps_siv linear noperspective v0.x, position
dcl_output o0.x
dcl_temps 12
lt r0.x, l(0.500000), cb0[6].x
lt r0.y, cb0[6].y, l(0.500000)
and r0.x, r0.y, r0.x
ge r0.yz, cb0[4].zzwz, l(0.000000, 0.000000, 0.000000, 0.000000)
and r0.x, r0.y, r0.x
and r0.x, r0.z, r0.x
ge r0.yz, l(0.000000, 1.000000, 1.000000, 0.000000), cb0[4].zzwz
and r0.x, r0.y, r0.x
and r0.x, r0.z, r0.x
if_nz r0.x
  ftoi r0.xy, cb0[5].yzyy
  ftoi r1.xy, cb0[1].xyxx
  iadd r1.xy, r1.xyxx, l(-1, -1, 0, 0)
  mov r0.zw, l(0,0,0,0)
  ld_indexable(texture2d)(float,float,float,float) r1.z, r0.xyww, t0.yzxw
  ld_indexable(texture2d)(float,float,float,float) r0.z, r0.xyzw, t1.yzxw
  lt r0.z, l(60000.000000), |r0.z|
  lt r0.w, l(0.500000), cb0[0].x
  mul r1.w, cb0[0].z, cb0[0].y
  add r2.x, -cb0[0].y, cb0[0].z
  mad r2.y, -r1.z, r2.x, cb0[0].z
  div r2.y, r1.w, r2.y
  mad r1.z, r1.z, r2.x, cb0[0].y
  movc r1.z, r0.w, r2.y, r1.z
  movc r0.z, r0.z, l(1000000015047466219876688855040.000000), r1.z
  max r1.z, r0.z, l(0.000010)
  mul r1.z, r1.z, l(0.020000)
  max r2.y, l(0.000010), cb0[5].x
  mul r2.y, r2.y, l(0.020000)
  div r2.zw, cb0[3].xxxy, cb0[1].xxxy
  mov r3.zw, l(0,0,0,0)
  mov r4.zw, l(0,0,0,0)
  mov r5.xyzw, l(0,0,0,0)
  mov r6.y, l(-1)
  loop
    ilt r6.z, l(1), r6.y
    breakc_nz r6.z
    mov r7.xy, r5.zxzz
    mov r8.xy, r5.wyww
    mov r6.x, l(-1)
    loop
      ilt r6.z, l(1), r6.x
      breakc_nz r6.z
      iadd r6.zw, r0.xxxy, r6.xxxy
      imax r6.zw, r6.zzzw, l(0, 0, 0, 0)
      imin r3.xy, r1.xyxx, r6.zwzz
      ld_indexable(texture2d)(float,float,float,float) r6.z, r3.xyww, t0.yzxw
      ld_indexable(texture2d)(float,float,float,float) r6.w, r3.xyww, t1.yzwx
      lt r6.w, l(60000.000000), |r6.w|
      mad r7.z, -r6.z, r2.x, cb0[0].z
      div r7.z, r1.w, r7.z
      mad r6.z, r6.z, r2.x, cb0[0].y
      movc r6.z, r0.w, r7.z, r6.z
      movc r6.z, r6.w, l(1000000015047466219876688855040.000000), r6.z
      add r6.z, -r0.z, r6.z
      lt r6.z, r1.z, |r6.z|
      if_nz r6.z
        iadd r6.z, r6.x, l(1)
        mov r6.x, r6.z
        continue
      endif
      itof r6.zw, r3.xxxy
      add r6.zw, r6.zzzw, -cb0[2].xxxy
      add r6.zw, r6.zzzw, l(0.000000, 0.000000, 0.500000, 0.500000)
      div r6.zw, r6.zzzw, cb0[1].xxxy
      add r6.zw, r6.zzzw, cb0[4].zzzw
      add r6.zw, r6.zzzw, -cb0[4].xxxy
      add r6.zw, r2.zzzw, r6.zzzw
      mad r6.zw, r6.zzzw, cb0[1].xxxy, l(0.000000, 0.000000, -0.500000, -0.500000)
      round_ni r7.zw, r6.zzzw
      ftoi r8.zw, r7.zzzw
      add r6.zw, r6.zzzw, -r7.zzzw
      add r7.zw, -r6.zzzw, l(0.000000, 0.000000, 1.000000, 1.000000)
      mov r9.xy, r8.xyxx
      mov r10.y, l(0)
      loop
        ige r9.z, r10.y, l(2)
        breakc_nz r9.z
        movc r9.z, r10.y, r6.w, r7.w
        mov r10.zw, r9.xxxy
        mov r10.x, l(0)
        loop
          ige r9.w, r10.x, l(2)
          breakc_nz r9.w
          iadd r11.xy, r8.zwzz, r10.xyxx
          imax r11.xy, r11.xyxx, l(0, 0, 0, 0)
          imin r4.xy, r1.xyxx, r11.xyxx
          ld_indexable(texture2d)(float,float,float,float) r9.w, r4.xyww, t2.yzwx
          add r9.w, r9.w, -cb0[5].x
          lt r9.w, r2.y, |r9.w|
          if_nz r9.w
            iadd r9.w, r10.x, l(1)
            mov r10.x, r9.w
            continue
          endif
          movc r9.w, r10.x, r6.z, r7.z
          mul r11.x, r9.z, r9.w
          mad r10.w, r9.w, r9.z, r10.w
          ld_indexable(texture2d)(float,float,float,float) r4.x, r4.xyzw, t4.xyzw
          mad r10.z, r4.x, r11.x, r10.z
          iadd r10.x, r10.x, l(1)
        endloop
        mov r9.xy, r10.zwzz
        iadd r10.y, r10.y, l(1)
      endloop
      mov r8.xy, r9.xyxx
      add r7.y, r7.y, l(1.000000)
      ld_indexable(texture2d)(float,float,float,float) r3.x, r3.xyzw, t3.xyzw
      add r7.x, r3.x, r7.x
      iadd r6.x, r6.x, l(1)
    endloop
    mov r5.xz, r7.yyxy
    mov r5.yw, r8.yyyx
    iadd r6.y, r6.y, l(1)
  endloop
  lt r0.xy, l(0.000000, 0.000000, 0.000000, 0.000000), r5.xyxx
  and r0.x, r0.y, r0.x
  div r0.yz, r5.zzwz, r5.xxyx
  add r0.y, -r0.z, r0.y
  and r0.y, r0.y, r0.x
else
  mov r0.xy, l(0,0,0,0)
endif
ftoi r0.z, v0.x
if_z r0.z
  mov o0.x, r0.y
else
  ieq r0.w, r0.z, l(1)
  if_nz r0.w
    and o0.x, r0.x, l(0x3f800000)
  else
    ieq r0.z, r0.z, l(2)
    if_nz r0.z
      lt r0.y, l(0.100000), |r0.y|
      and r0.x, r0.y, r0.x
      and o0.x, r0.x, l(0x3f800000)
    else
      resinfo_indexable(texture2d)(float,float,float,float)_uint r0.x, l(0), t0.xyzw
      resinfo_indexable(texture2d)(float,float,float,float)_uint r0.y, l(0), t1.yxzw
      iadd r0.x, r0.y, r0.x
      resinfo_indexable(texture2d)(float,float,float,float)_uint r0.y, l(0), t2.yxzw
      iadd r0.x, r0.y, r0.x
      resinfo_indexable(texture2d)(float,float,float,float)_uint r0.y, l(0), t3.yxzw
      iadd r0.x, r0.y, r0.x
      resinfo_indexable(texture2d)(float,float,float,float)_uint r0.y, l(0), t4.yxzw
      iadd r0.x, r0.y, r0.x
      itof r0.x, r0.x
      add r0.x, r0.x, cb0[3].x
      mul o0.x, r0.x, l(0.000000)
    endif
  endif
endif
ret
";

        /// <summary>The probe under IEEE strictness, 0xA000.</summary>
        internal const string ProbeStrict = @"
ps_5_0
dcl_globalFlags refactoringAllowed
dcl_constantbuffer CB0[7], immediateIndexed
dcl_resource_texture2d (float,float,float,float) t0
dcl_resource_texture2d (float,float,float,float) t1
dcl_resource_texture2d (float,float,float,float) t2
dcl_resource_texture2d (float,float,float,float) t3
dcl_resource_texture2d (float,float,float,float) t4
dcl_input_ps_siv linear noperspective v0.x, position
dcl_output o0.x
dcl_temps 12
lt [precise(x)] r0.x, l(0.500000), cb0[6].x
lt [precise(y)] r0.y, cb0[6].y, l(0.500000)
and [precise(x)] r0.x, r0.y, r0.x
ge [precise(yz)] r0.yz, cb0[4].zzwz, l(0.000000, 0.000000, 0.000000, 0.000000)
and [precise(x)] r0.x, r0.y, r0.x
and [precise(x)] r0.x, r0.z, r0.x
ge [precise(yz)] r0.yz, l(0.000000, 1.000000, 1.000000, 0.000000), cb0[4].zzwz
and [precise(x)] r0.x, r0.y, r0.x
and [precise(x)] r0.x, r0.z, r0.x
if_nz r0.x
  ftoi [precise(xy)] r0.xy, cb0[5].yzyy
  ftoi [precise(xy)] r1.xy, cb0[1].xyxx
  iadd [precise(xy)] r1.xy, r1.xyxx, l(-1, -1, 0, 0)
  mov [precise(zw)] r0.zw, l(0,0,0,0)
  ld_indexable [precise](texture2d)(float,float,float,float) r1.z, r0.xyww, t0.yzxw
  ld_indexable [precise(z)](texture2d)(float,float,float,float) r0.z, r0.xyzw, t1.yzxw
  lt [precise(z)] r0.z, l(60000.000000), |r0.z|
  lt [precise(w)] r0.w, l(0.500000), cb0[0].x
  mul [precise(w)] r1.w, cb0[0].z, cb0[0].y
  add [precise(x)] r2.x, -cb0[0].y, cb0[0].z
  mad [precise(y)] r2.y, -r1.z, r2.x, cb0[0].z
  div [precise(y)] r2.y, r1.w, r2.y
  mad [precise(z)] r1.z, r1.z, r2.x, cb0[0].y
  movc [precise(z)] r1.z, r0.w, r2.y, r1.z
  movc [precise(z)] r0.z, r0.z, l(1000000015047466219876688855040.000000), r1.z
  max [precise(z)] r1.z, r0.z, l(0.000010)
  mul [precise(z)] r1.z, r1.z, l(0.020000)
  max [precise(y)] r2.y, l(0.000010), cb0[5].x
  mul [precise(y)] r2.y, r2.y, l(0.020000)
  div [precise(zw)] r2.zw, cb0[3].xxxy, cb0[1].xxxy
  mov [precise(zw)] r3.zw, l(0,0,0,0)
  mov [precise(zw)] r4.zw, l(0,0,0,0)
  mov [precise] r5.xyzw, l(0,0,0,0)
  mov [precise(y)] r6.y, l(-1)
  loop
    ilt [precise(z)] r6.z, l(1), r6.y
    breakc_nz r6.z
    mov [precise(xy)] r7.xy, r5.zxzz
    mov [precise(xy)] r8.xy, r5.wyww
    mov [precise(x)] r6.x, l(-1)
    loop
      ilt [precise(z)] r6.z, l(1), r6.x
      breakc_nz r6.z
      iadd [precise(zw)] r6.zw, r0.xxxy, r6.xxxy
      imax [precise(zw)] r6.zw, r6.zzzw, l(0, 0, 0, 0)
      imin [precise(xy)] r3.xy, r1.xyxx, r6.zwzz
      ld_indexable [precise](texture2d)(float,float,float,float) r6.z, r3.xyww, t0.yzxw
      ld_indexable [precise(w)](texture2d)(float,float,float,float) r6.w, r3.xyww, t1.yzwx
      lt [precise(w)] r6.w, l(60000.000000), |r6.w|
      mad [precise(z)] r7.z, -r6.z, r2.x, cb0[0].z
      div [precise(z)] r7.z, r1.w, r7.z
      mad [precise(z)] r6.z, r6.z, r2.x, cb0[0].y
      movc [precise(z)] r6.z, r0.w, r7.z, r6.z
      movc [precise(z)] r6.z, r6.w, l(1000000015047466219876688855040.000000), r6.z
      add [precise(z)] r6.z, -r0.z, r6.z
      ge [precise(z)] r6.z, r1.z, |r6.z|
      if_z r6.z
        iadd [precise(z)] r6.z, r6.x, l(1)
        mov [precise(x)] r6.x, r6.z
        continue
      endif
      itof [precise(zw)] r6.zw, r3.xxxy
      add [precise(zw)] r6.zw, r6.zzzw, l(0.000000, 0.000000, 0.500000, 0.500000)
      add [precise(zw)] r6.zw, r6.zzzw, -cb0[2].xxxy
      div [precise(zw)] r6.zw, r6.zzzw, cb0[1].xxxy
      add [precise(zw)] r6.zw, r6.zzzw, cb0[4].zzzw
      add [precise(zw)] r6.zw, r6.zzzw, -cb0[4].xxxy
      add [precise(zw)] r6.zw, r2.zzzw, r6.zzzw
      mad [precise(zw)] r6.zw, r6.zzzw, cb0[1].xxxy, l(0.000000, 0.000000, -0.500000, -0.500000)
      round_ni [precise(zw)] r7.zw, r6.zzzw
      ftoi [precise(zw)] r7.zw, r7.zzzw
      itof [precise(zw)] r8.zw, r7.zzzw
      add [precise(zw)] r6.zw, r6.zzzw, -r8.zzzw
      add [precise(zw)] r8.zw, -r6.zzzw, l(0.000000, 0.000000, 1.000000, 1.000000)
      mov [precise(xy)] r9.xy, r8.xyxx
      mov [precise(y)] r10.y, l(0)
      loop
        ige [precise(z)] r9.z, r10.y, l(2)
        breakc_nz r9.z
        movc [precise(z)] r9.z, r10.y, r6.w, r8.w
        mov [precise(zw)] r10.zw, r9.xxxy
        mov [precise(x)] r10.x, l(0)
        loop
          ige [precise(w)] r9.w, r10.x, l(2)
          breakc_nz r9.w
          iadd [precise(xy)] r11.xy, r7.zwzz, r10.xyxx
          imax [precise(xy)] r11.xy, r11.xyxx, l(0, 0, 0, 0)
          imin [precise(xy)] r4.xy, r1.xyxx, r11.xyxx
          ld_indexable [precise(w)](texture2d)(float,float,float,float) r9.w, r4.xyww, t2.yzwx
          add [precise(w)] r9.w, r9.w, -cb0[5].x
          ge [precise(w)] r9.w, r2.y, |r9.w|
          if_z r9.w
            iadd [precise(w)] r9.w, r10.x, l(1)
            mov [precise(x)] r10.x, r9.w
            continue
          endif
          movc [precise(w)] r9.w, r10.x, r6.z, r8.z
          mad [precise(w)] r10.w, r9.w, r9.z, r10.w
          ld_indexable [precise(x)](texture2d)(float,float,float,float) r4.x, r4.xyzw, t4.xyzw
          mul [precise(y)] r4.y, r9.z, r9.w
          mad [precise(z)] r10.z, r4.x, r4.y, r10.z
          iadd [precise(x)] r10.x, r10.x, l(1)
        endloop
        mov [precise(xy)] r9.xy, r10.zwzz
        iadd [precise(y)] r10.y, r10.y, l(1)
      endloop
      mov [precise(xy)] r8.xy, r9.xyxx
      add [precise(y)] r7.y, r7.y, l(1.000000)
      ld_indexable [precise(x)](texture2d)(float,float,float,float) r3.x, r3.xyzw, t3.xyzw
      add [precise(x)] r7.x, r3.x, r7.x
      iadd [precise(x)] r6.x, r6.x, l(1)
    endloop
    mov [precise(xz)] r5.xz, r7.yyxy
    mov [precise(yw)] r5.yw, r8.yyyx
    iadd [precise(y)] r6.y, r6.y, l(1)
  endloop
  lt [precise(xy)] r0.xy, l(0.000000, 0.000000, 0.000000, 0.000000), r5.xyxx
  and [precise(x)] r0.x, r0.y, r0.x
  div [precise(yz)] r0.yz, r5.zzwz, r5.xxyx
  add [precise(y)] r0.y, -r0.z, r0.y
  and [precise(y)] r0.y, r0.y, r0.x
else
  mov [precise(xy)] r0.xy, l(0,0,0,0)
endif
ftoi [precise(z)] r0.z, v0.x
if_z r0.z
  mov [precise(x)] o0.x, r0.y
else
  ieq [precise(w)] r0.w, r0.z, l(1)
  if_nz r0.w
    and [precise(x)] o0.x, r0.x, l(0x3f800000)
  else
    ieq [precise(z)] r0.z, r0.z, l(2)
    if_nz r0.z
      lt [precise(y)] r0.y, l(0.100000), |r0.y|
      and [precise(x)] r0.x, r0.y, r0.x
      and [precise(x)] o0.x, r0.x, l(0x3f800000)
    else
      resinfo_indexable [precise(xyw)](texture2d)(float,float,float,float)_uint r0.x, l(0), t0.xyzw
      resinfo_indexable [precise(xyw)](texture2d)(float,float,float,float)_uint r0.y, l(0), t1.yxzw
      iadd [precise(x)] r0.x, r0.y, r0.x
      resinfo_indexable [precise(xyw)](texture2d)(float,float,float,float)_uint r0.y, l(0), t2.yxzw
      iadd [precise(x)] r0.x, r0.y, r0.x
      resinfo_indexable [precise(xyw)](texture2d)(float,float,float,float)_uint r0.y, l(0), t3.yxzw
      iadd [precise(x)] r0.x, r0.y, r0.x
      resinfo_indexable [precise(xyw)](texture2d)(float,float,float,float)_uint r0.y, l(0), t4.yxzw
      iadd [precise(x)] r0.x, r0.y, r0.x
      itof [precise(x)] r0.x, r0.x
      add [precise(x)] r0.x, r0.x, cb0[3].x
      mul [precise(x)] o0.x, r0.x, l(0.000000)
    endif
  endif
endif
ret
";
    }
}
