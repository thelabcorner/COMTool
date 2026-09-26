namespace ComTool.Runtime;

public static class CoreOperations
{
    public static OperationCatalog CreateCatalog() =>
        BuiltInOperations.Catalog;
}
