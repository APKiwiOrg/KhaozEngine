namespace KhaozEngine.Ecs;

public interface ISystem
{
    void Update(World world, float dt);
}
