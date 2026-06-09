using KhaozEngine.Content;

if (args.Length < 1)
{
    System.Console.Error.WriteLine("Usage: validate <DataDir>");
    return 1;
}

return JsonSchemaValidator.ValidateDirectory(args[0], System.Console.Out) ? 0 : 1;
