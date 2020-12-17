using System;
using System.Collections.Generic;
using System.Linq;
using HotChocolate;
using HotChocolate.Language;
using HotChocolate.Types;
using StrawberryShake.CodeGeneration.Analyzers.Models;
using StrawberryShake.CodeGeneration.Types;
using StrawberryShake.CodeGeneration.Utilities;
using FieldSelection = StrawberryShake.CodeGeneration.Utilities.FieldSelection;
using static StrawberryShake.CodeGeneration.Utilities.NameUtils;

namespace StrawberryShake.CodeGeneration.Analyzers
{
    public partial class DocumentAnalyzer
    {
        private static readonly UnionTypeSelectionSetAnalyzer _unionTypeSelectionSetAnalyzer =
            new();
        private static readonly InterfaceTypeSelectionSetAnalyzer _interfaceTypeSelectionSetAnalyzer =
            new();
        private static readonly ObjectTypeSelectionSetAnalyzer _objectTypeSelectionSetAnalyzer =
            new();

        private static void CollectOutputTypes(
            IDocumentAnalyzerContext context,
            DocumentNode document)
        {
            context.SetDocument(document);

            var backlog = new Queue<FieldSelection>();

            foreach (OperationDefinitionNode operation in
                document.Definitions.OfType<OperationDefinitionNode>())
            {
                NamePathSegment root = Path.New(operation.Name!.Value);

                ObjectType operationType = context.Schema.GetOperationType(operation.Operation);

                VisitOperationSelectionSet(context, operation, operationType, root, backlog);

                while (backlog.Any())
                {
                    FieldSelection current = backlog.Dequeue();
                    Path path = current.Path.Append(current.ResponseName);

                    if (!current.Field.Type.NamedType().IsLeafType())
                    {
                        VisitFieldSelectionSet(
                            context, operation, current.FieldSyntax,
                            current.Field.Type, path, backlog);
                    }
                }

                RegisterOperationModel(context, document, operation);
            }
        }

        private static void VisitOperationSelectionSet(
            IDocumentAnalyzerContext context,
            OperationDefinitionNode operation,
            ObjectType operationType,
            Path path,
            Queue<FieldSelection> backlog)
        {
            SelectionVariants selectionVariants =
                context.CollectFields(
                    operationType,
                    operation.SelectionSet,
                    path);

            EnqueueFields(backlog, selectionVariants.ReturnType.Fields, path);

            _objectTypeSelectionSetAnalyzer.Analyze(
                context,
                operation,
                new FieldNode(
                    null,
                    new NameNode(operation.Name!.Value),
                    null,
                    new[]
                    {
                        new DirectiveNode(
                            GeneratorDirectives.Type,
                            new ArgumentNode("name", operation.Name.Value)),
                        new DirectiveNode(GeneratorDirectives.Operation)
                    },
                    Array.Empty<ArgumentNode>(),
                    null),
                selectionVariants,
                new NonNullType(operationType),
                operationType,
                path);
        }

        private static void VisitFieldSelectionSet(
            IDocumentAnalyzerContext context,
            OperationDefinitionNode operation,
            FieldNode fieldSelection,
            IType fieldType,
            Path path,
            Queue<FieldSelection> backlog)
        {
            var namedType = (INamedOutputType)fieldType.NamedType();

            SelectionVariants selectionVariants =
                context.CollectFields(
                    namedType,
                    fieldSelection.SelectionSet!,
                    path);

            foreach (Selection selectionInfo in selectionVariants.Variants)
            {
                EnqueueFields(backlog, selectionInfo.Fields, path);
            }

            if (namedType is UnionType unionType)
            {
                _unionTypeSelectionSetAnalyzer.Analyze(
                    context,
                    operation,
                    fieldSelection,
                    selectionVariants,
                    fieldType,
                    unionType,
                    path);
            }
            else if (namedType is InterfaceType interfaceType)
            {
                _interfaceTypeSelectionSetAnalyzer.Analyze(
                    context,
                    operation,
                    fieldSelection,
                    selectionVariants,
                    fieldType,
                    interfaceType,
                    path);
            }
            else if (namedType is ObjectType objectType)
            {
                _objectTypeSelectionSetAnalyzer.Analyze(
                    context,
                    operation,
                    fieldSelection,
                    selectionVariants,
                    fieldType,
                    objectType,
                    path);
            }
        }

        private static void RegisterOperationModel(
            IDocumentAnalyzerContext context,
            DocumentNode document,
            OperationDefinitionNode operationDefinition)
        {
            OutputTypeModel returnType =
                context.Types.OfType<OutputTypeModel>()
                    .First(t => t.SelectionSet == operationDefinition.SelectionSet && t.IsInterface);

            var parser = new ParserModel(
                context.GetOrCreateName(
                    GetClassName(operationDefinition.Name!.Value, "ResultParser")),
                operationDefinition,
                returnType,
                context.FieldParsers.Where(t => t.Operation == operationDefinition).ToList());

            var operation = new OperationModel(
                returnType.Name,
                context.Schema.GetOperationType(operationDefinition.Operation),
                document,
                operationDefinition,
                parser,
                CreateOperationArguments(context, operationDefinition));

            context.Register(operation);
        }

        private static IReadOnlyList<ArgumentModel> CreateOperationArguments(
            IDocumentAnalyzerContext context,
            OperationDefinitionNode operationDefinition)
        {
            var arguments = new List<ArgumentModel>();

            foreach (VariableDefinitionNode variableDefinition in
                operationDefinition.VariableDefinitions)
            {

                INamedInputType namedInputType = context.Schema.GetType<INamedInputType>(
                    variableDefinition.Type.NamedType().Name.Value);

                arguments.Add(new ArgumentModel(
                    variableDefinition.Variable.Name.Value,
                    (IInputType)variableDefinition.Type.ToType(namedInputType),
                    variableDefinition,
                    variableDefinition.DefaultValue));
            }

            return arguments;
        }

        private static void EnqueueFields(
            Queue<FieldSelection> backlog,
            IEnumerable<FieldSelection> fieldSelections,
            Path path)
        {
            foreach (FieldSelection fieldSelection in fieldSelections)
            {
                backlog.Enqueue(new FieldSelection(
                    fieldSelection.Field,
                    fieldSelection.FieldSyntax,
                    path));
            }
        }
    }
}