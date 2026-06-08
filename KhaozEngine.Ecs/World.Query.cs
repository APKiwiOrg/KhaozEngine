namespace KhaozEngine.Ecs;

public sealed partial class World
{
    /// <summary>Starts a filtered query.</summary>
    public Query Query() => new(this);

    public void ForEach<T1>(RefAction<T1> a) where T1 : struct, IComponent => new Query(this).ForEach(a);

    public void ForEach<T1, T2>(RefAction<T1, T2> a)
        where T1 : struct, IComponent where T2 : struct, IComponent => new Query(this).ForEach(a);

    public void ForEach<T1, T2, T3>(RefAction<T1, T2, T3> a)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        => new Query(this).ForEach(a);

    public void ForEach<T1, T2, T3, T4>(RefAction<T1, T2, T3, T4> a)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent => new Query(this).ForEach(a);

    public void ForEach<T1, T2, T3, T4, T5>(RefAction<T1, T2, T3, T4, T5> a)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent => new Query(this).ForEach(a);

    public void ForEach<T1, T2, T3, T4, T5, T6>(RefAction<T1, T2, T3, T4, T5, T6> a)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
        => new Query(this).ForEach(a);

    public void ForEach<T1, T2, T3, T4, T5, T6, T7>(RefAction<T1, T2, T3, T4, T5, T6, T7> a)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
        where T7 : struct, IComponent => new Query(this).ForEach(a);

    public void ForEach<T1, T2, T3, T4, T5, T6, T7, T8>(RefAction<T1, T2, T3, T4, T5, T6, T7, T8> a)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent where T6 : struct, IComponent
        where T7 : struct, IComponent where T8 : struct, IComponent => new Query(this).ForEach(a);
}
