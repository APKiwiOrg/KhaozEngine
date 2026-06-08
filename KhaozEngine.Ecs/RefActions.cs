namespace KhaozEngine.Ecs;

public delegate void RefAction<T1>(Entity e, ref T1 c1);
public delegate void RefAction<T1, T2>(Entity e, ref T1 c1, ref T2 c2);
public delegate void RefAction<T1, T2, T3>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3);
public delegate void RefAction<T1, T2, T3, T4>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4);
public delegate void RefAction<T1, T2, T3, T4, T5>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5);
public delegate void RefAction<T1, T2, T3, T4, T5, T6>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6);
public delegate void RefAction<T1, T2, T3, T4, T5, T6, T7>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6, ref T7 c7);
public delegate void RefAction<T1, T2, T3, T4, T5, T6, T7, T8>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6, ref T7 c7, ref T8 c8);
