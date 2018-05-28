using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Net.Derid.Interfaces.Models;
using Net.Derid.Mq.EasyNetQAdapter;
using Net.Derid.ProxyGenerator.Model;
using Request = Net.Derid.Interfaces.Models.Request;
using Response = Net.Derid.Interfaces.Models.Response;

using IBusConnector = Net.Derid.Interfaces.IBusConnector;
using Net.Derid.Interfaces;
using System.Text;

namespace Net.Derid.ProxyGenerator
{
    public partial class ProxyGenerator : IProxyGenerator
    {
        private readonly ITypeAnalyzer _typeAnalyzer;
        private readonly IBusConnector _bus;

        public ProxyGenerator(ITypeAnalyzer typeAnalyzer, IBusConnector bus)
        {
            _bus = bus;
            _typeAnalyzer = typeAnalyzer;
        }

        public DynamicAssemblyContainer<T> GenerateClient<T>() where T : class
        {
            return GenerateClient<T, Request, Response>();
        }

        public DynamicAssemblyContainer<T> GenerateClient<T, TRequest, TResponse>() where T : class
        {
            var clientInterface = typeof(T);
            if (!clientInterface.IsInterface)
                throw new InvalidOperationException("Passed parameter is not an interface, but proxy generator expects an interface.");
            var workspace = new AdhocWorkspace();

            
            var generator = SyntaxGenerator.GetGenerator(workspace, LanguageNames.CSharp);
            
            

            var messageTypesContainer = new MessageTypesContainer()
            {
                RequestType = typeof(TRequest).Name,
                ResponseType = typeof(TResponse).Name
            };
            var typeStructure = _typeAnalyzer.AnalyzeType(clientInterface);

            
            var usings = new List<SyntaxNode>
            {
                generator.NamespaceImportDeclaration("System"),
                generator.NamespaceImportDeclaration("System.Collections"),
                generator.NamespaceImportDeclaration("System.Collections.Generic"),
                generator.NamespaceImportDeclaration(typeStructure.Namespace),
                generator.NamespaceImportDeclaration("Net.Derid.Interfaces.Models"),
                generator.NamespaceImportDeclaration("System.Threading.Tasks"),
                generator.NamespaceImportDeclaration("Net.Derid.Mq.EasyNetQAdapter"),
                generator.NamespaceImportDeclaration("Net.Derid.Runtime"),
                generator.NamespaceImportDeclaration("Net.Derid.Runtime.Dependency"),
                generator.NamespaceImportDeclaration("EasyNetQ"),
                generator.NamespaceImportDeclaration("IdentityServer4"),
                generator.NamespaceImportDeclaration("Newtonsoft.Json"),
                generator.NamespaceImportDeclaration("Newtonsoft.Json.Linq"),
                
        };

            var methods = new List<MethodDeclarationSyntax>();
            foreach (var typeStructureMethod in typeStructure.Methods)
            {
                var method = GetMethodDeclarationSyntax<T>(typeStructureMethod, messageTypesContainer.RequestType, messageTypesContainer.ResponseType);
                methods.Add(method);
            }

            methods.Add(GetDisposeMethod());


            var eventInfos = clientInterface.GetEvents();
            var eventsDeclarations = new List<EventFieldDeclarationSyntax>();
            var eventsGetEventFromBackListenerMethods = new List<MethodDeclarationSyntax>();

            var eventNamespacesWithMessageClass = new List<SyntaxNode>();

            if (eventInfos != null && eventInfos.Length > 0)
            {

                foreach (var @event in eventInfos)
                {
                    var eventTypeName = @event.EventHandlerType.FullName;
                    var eventField = SyntaxFactory.EventFieldDeclaration(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.ParseTypeName(eventTypeName),
                            SyntaxFactory.SeparatedList(new[] { SyntaxFactory.VariableDeclarator(@event.Name) })
                        )
                    ).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

                    
                    var eventHandlerType = @event.EventHandlerType.GetMethod("Invoke");
                    var parameters = eventHandlerType.GetParameters();
                    var transfaredDataType = parameters[0].ParameterType;

                    eventNamespacesWithMessageClass.Add(EventHandlerContainerFabric.ReturnNamespaceWithEventMessage(@event, transfaredDataType));

                    eventsDeclarations.Add(eventField);

                    eventsGetEventFromBackListenerMethods.Add(GetEventFromBackListenerDeclarationSyntax(@event));
                }
            }

            foreach (var eventMessageUsing in UsingsList)
            {
                usings.Add(generator.NamespaceImportDeclaration(eventMessageUsing));
            }

            var constructor = GetConstructor(generator, typeStructure, eventInfos);

            
            var members = new List<SyntaxNode>
            {
                constructor
            };

            

            var fields = new List<FieldDeclarationSyntax>();
            var busField = SyntaxFactory.FieldDeclaration(
                    SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.ParseTypeName("Net.Derid.Interfaces.IBusConnector"),
                        SyntaxFactory.SeparatedList(new[] { SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier("_bus")) })))
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));

            fields.AddRange(new[] {
                busField,
            });
            members.AddRange(fields);

            members.AddRange(eventsDeclarations);
            members.AddRange(eventsGetEventFromBackListenerMethods);

            members.AddRange(methods);



            
            var className = typeStructure.Name.Substring(1, typeStructure.Name.Length - 1) + Constants.ClassNameSuffix;
            var classDefinition = generator.ClassDeclaration(
                className,
                typeParameters: null,
                accessibility: Accessibility.Public,
                modifiers: DeclarationModifiers.None, 
                                                      
                interfaceTypes: new[] { generator.IdentifierName(typeStructure.Name), generator.IdentifierName("IDisposable") },
                members: members).NormalizeWhitespace();

            
            var namespaceDeclaration = generator.NamespaceDeclaration(typeStructure.Namespace + ".Generated", classDefinition);
            

            
            var declarations = new List<SyntaxNode>();
            declarations.AddRange(usings);
            declarations.AddRange(eventNamespacesWithMessageClass);
            declarations.Add(namespaceDeclaration);
            
            var newNode = generator.CompilationUnit(declarations).NormalizeWhitespace();

            var entryProjectAssembly = Assembly.GetAssembly(typeof(T)).GetName().Name;
            var trustedAssembliesPaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator);
            var neededAssemblies = new List<string>
            {
                "System.Runtime",
                "System.Reflection",
                "mscorlib",
                "netstandard",
                "System.Collections",
                "Net.Derid.Mq.EasyNetQAdapter",
                "Net.Derid.Interfaces",
                "IdentityServer4",
                "Net.Derid.Runtime",
                "Net.Derid.Runtime.Dependency.StructureMap",
                
                "EasyNetQ",
                "MassTransit",
                "MassTransit.RabbitMQ",
                "Newtonsoft.Json",
                entryProjectAssembly, 
            };
            neededAssemblies.AddRange(UsingsList);

            var references = trustedAssembliesPaths
                .Where(p => neededAssemblies.Contains(Path.GetFileNameWithoutExtension(p)))
                .Select(p => MetadataReference.CreateFromFile(p))
                .ToList();

            var basicType = typeof(SearchOptions);
            var assemblyName = className + "Assembly";

            CSharpCompilation compilation = CSharpCompilation.Create(
                    assemblyName,
                    new[] {newNode.SyntaxTree},
                    new[] {MetadataReference.CreateFromFile(typeof(object).Assembly.Location)},
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddReferences(new List<MetadataReference>()
                {
                    MetadataReference.CreateFromFile(basicType.Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(EasyNetQBusAdapter).Assembly.Location),
                })
                .AddReferences(references);

            
            GeneratedCodeFileOutput(className + ".tcs", classDefinition.ToFullString());
            //

            using (var dllStream = new MemoryStream())
            {
                var emitResult2 = compilation.Emit(dllStream);
                if (!emitResult2.Success)
                {
                    StringBuilder sb = new StringBuilder();

                    sb.Append(compilation.SyntaxTrees[0]);

                    sb.AppendLine();
                    sb.Append("
                    sb.AppendLine();

                    foreach (var error in emitResult2.Diagnostics)
                    {
                        sb.Append("
                        sb.AppendLine();
                    }

                    sb.Append("

                    GeneratedCodeFileOutput("GeneratorErrorOutput.cs", sb.ToString());
                }

                var emittedAssembly = Assembly.Load(dllStream.ToArray());
                var assemblyStream = dllStream;

                var type = emittedAssembly.DefinedTypes.First(x => x.Name == className);
                var requestType = emittedAssembly.DefinedTypes.FirstOrDefault(x => x.Name == messageTypesContainer.RequestType);
                var responseType = emittedAssembly.DefinedTypes.FirstOrDefault(x => x.Name == messageTypesContainer.ResponseType);
                var instance = Activator.CreateInstance(type, _bus);
                var proxy = instance as T;
                var result = new DynamicAssemblyContainer<T>()
                {
                    Proxy = proxy,
                    DynamicAssembly = emittedAssembly,
                    DynamicAssemblyStream = assemblyStream,
                    RequestType = requestType,
                    ResponseType = responseType
                };
                return result;
            }
        }

        private string GetResultTypeOmittingTask(string type)
        {
            if (type == "System.Threading.Tasks.Task" || type == "Task")
                return "object";
            if (type.StartsWith("System.Threading.Tasks.Task<") || type.StartsWith("Task<"))
            {
                type = type.Replace("System.Threading.Tasks.Task<", string.Empty);
                type = type.Replace("Task<", string.Empty);
                type = type.Substring(0, type.Length - 1);
            }
            return type;
        }

        private MethodDeclarationSyntax GetMethodDeclarationSyntax<T>(MethodStructure methodStructure, string requestMessageType, string responseMessageType) where T:class
        {
            var parameterList = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(GetParametersList(methodStructure.Params)));

            var body = GetMethodBody<T>(methodStructure, requestMessageType, responseMessageType);

            return SyntaxFactory.MethodDeclaration(
                    attributeLists: SyntaxFactory.List<AttributeListSyntax>(),
                    modifiers: SyntaxFactory.TokenList(),
                    returnType: SyntaxFactory.ParseTypeName(methodStructure.ReturnTypeName),
                    explicitInterfaceSpecifier: null,
                    identifier: SyntaxFactory.Identifier(methodStructure.Name),
                    typeParameterList: null,
                    parameterList: parameterList,
                    constraintClauses: SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    body: body,
                    semicolonToken: SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                
                .WithAdditionalAnnotations(Formatter.Annotation)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
        }

        private MethodDeclarationSyntax GetDisposeMethod()
        {
            var line1 = SyntaxFactory.ParseStatement("_bus?.Dispose();");
            var body = SyntaxFactory.Block(line1);

            return  SyntaxFactory.MethodDeclaration(
                    attributeLists: SyntaxFactory.List<AttributeListSyntax>(),
                    modifiers: SyntaxFactory.TokenList(),
                    returnType: SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                    explicitInterfaceSpecifier: null,
                    identifier: SyntaxFactory.Identifier("Dispose"),
                    typeParameterList: null,
                    parameterList: SyntaxFactory.ParameterList(),
                    constraintClauses: SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    body: body,
                    semicolonToken: SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .WithAdditionalAnnotations(Formatter.Annotation)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
        }

        private MethodDeclarationSyntax GetEventFromBackListenerDeclarationSyntax(EventInfo info)
        {
            var methodName = GetEventListenerMethodName(info);
            var body = GetEventFromBackListener(info);

            return SyntaxFactory.MethodDeclaration(
                attributeLists: SyntaxFactory.List<AttributeListSyntax>(),
                modifiers: SyntaxFactory.TokenList(),
                returnType: SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                explicitInterfaceSpecifier: null,
                identifier: SyntaxFactory.Identifier(methodName),
                typeParameterList: null,
                parameterList: SyntaxFactory.ParameterList(), 
                constraintClauses: SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                body: body,
                semicolonToken: SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                    .WithAdditionalAnnotations(Formatter.Annotation)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));
        }

        private IEnumerable<ParameterSyntax> GetParametersList(IEnumerable<ParamStructure> paramStructures)
        {
            foreach (var paramStructure in paramStructures)
            {
                yield return SyntaxFactory.Parameter(attributeLists: SyntaxFactory.List<AttributeListSyntax>(),
                    modifiers: SyntaxFactory.TokenList(),
                    type: SyntaxFactory.ParseTypeName(paramStructure.TypeName),
                    identifier: SyntaxFactory.Identifier(paramStructure.Name),
                    @default: null);
            }
        }

        private string GetEventListenerMethodName(EventInfo info)
        {
            return "On" + info.Name + "Invoked";
        }

        
        private static void GeneratedCodeFileOutput(string fileName, string definition)
        {
            var currentProjectDirectory = Directory.GetCurrentDirectory();
            var generationLogFileFolderPath = Path.Combine(currentProjectDirectory, "Logs", "Generation");

            if (!Directory.Exists(generationLogFileFolderPath))
            {
                Directory.CreateDirectory(generationLogFileFolderPath);
            }

            string finishName;
            if (string.IsNullOrEmpty(fileName))
            {
                var now = DateTime.Now;
                var timeSuffix = $"_{now.ToString("MMMM")}_{now.Day}_{now.Hour}h_{now.Minute}m";
                finishName = "CompletedProxyGeneratorOutput" + timeSuffix + ".cs";
            }
            else
            {
                finishName = fileName;
            }

            var pathToLogFile = Path.Combine(generationLogFileFolderPath, finishName);

            while(File.Exists(pathToLogFile))
            {
                File.Delete(pathToLogFile);
            }

            using (FileStream fs = File.Create(pathToLogFile))
            {
                Byte[] info = new UTF8Encoding(true).GetBytes(definition);
                fs.Write(info, 0, info.Length);
            }
        }
        
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Net.Derid.Interfaces.Models;
using Net.Derid.ProxyGenerator.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Net.Derid.ProxyGenerator
{
    public partial class ProxyGenerator
    {
        private MessageTypesContainer GenerateMessageTypes<T, TRequest, TResponse>() where T : class
        {
            var clientInterface = typeof(T);
            if (!clientInterface.IsInterface)
                throw new InvalidOperationException("Passed parameter is not an interface, but proxy generator expects an interface.");

            string baseRequestType = typeof(TRequest).Name;
            string baseResponseType = typeof(TResponse).Name;

            var namespaceName = typeof(T).Namespace + ".Generated";
            var typeName = typeof(T).Name;
            var requestClassName = typeName.Substring(1, typeName.Length - 1) + baseRequestType;
            var responseClassName = typeName.Substring(1, typeName.Length - 1) + baseResponseType;
            var workspace = new AdhocWorkspace();

            var generator = SyntaxGenerator.GetGenerator(workspace, LanguageNames.CSharp);

            var declarations = new List<SyntaxNode>();
            var usings = new List<SyntaxNode>
            {
                generator.NamespaceImportDeclaration("System"),
                generator.NamespaceImportDeclaration("Net.Derid.Interfaces.Models"),
            };
            declarations.AddRange(usings);

            var requestClassDefinition = generator.ClassDeclaration(
                requestClassName,
                typeParameters: null,
                accessibility: Accessibility.Public,
                modifiers: DeclarationModifiers.None, 
                baseType: generator.IdentifierName(baseRequestType)
                );
            var responseClassDefinition = generator.ClassDeclaration(
                responseClassName,
                typeParameters: null,
                accessibility: Accessibility.Public,
                modifiers: DeclarationModifiers.None, 
                baseType: generator.IdentifierName(baseResponseType)
            );

            var methodName = "OnResponse";

            var parameterList = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(new List<ParameterSyntax>() { SyntaxFactory.Parameter(attributeLists: SyntaxFactory.List<AttributeListSyntax>(),
                modifiers: SyntaxFactory.TokenList(),
                type: SyntaxFactory.ParseTypeName(requestClassName),
                identifier: SyntaxFactory.Identifier("request"),
                @default: null)
            }));

            var line3 = SyntaxFactory.ParseStatement("var result = new List<SampleUser>() { new SampleUser() {Id=Guid.NewGuid(), Firstname=\"FName test\", Lastname=\"LName test\" } };");
            var line4 = SyntaxFactory.ParseStatement("var serializedObject = JsonConvert.SerializeObject(result);");
            var line5 = SyntaxFactory.ParseStatement($"return new {responseClassName}() {{Success=true, Result = result, ResultSerialized = serializedObject}};");
            var body = SyntaxFactory.Block(line3, line4, line5);

            var handlerMethod = SyntaxFactory.MethodDeclaration(attributeLists: SyntaxFactory.List<AttributeListSyntax>(),
                    modifiers: SyntaxFactory.TokenList(new List<SyntaxToken>() { SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                        
                        
                    }),
                    returnType: SyntaxFactory.ParseTypeName(responseClassName),
                    explicitInterfaceSpecifier: null,
                    identifier: SyntaxFactory.Identifier(methodName),
                    typeParameterList: null,
                    parameterList: parameterList,
                    constraintClauses: SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    body: body,
                    semicolonToken: SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                
                .WithAdditionalAnnotations(Formatter.Annotation);


            var returnLine = SyntaxFactory.ParseStatement($"return OnResponse;");
            var getFuncBody = SyntaxFactory.Block(returnLine);

            var getFuncMethod = SyntaxFactory.MethodDeclaration(attributeLists: SyntaxFactory.List<AttributeListSyntax>(),
                    modifiers: SyntaxFactory.TokenList(new List<SyntaxToken>() { SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                        
                    }),
                    returnType: SyntaxFactory.ParseTypeName($"Func<{requestClassName}, {responseClassName}>"),
                    explicitInterfaceSpecifier: null,
                    identifier: SyntaxFactory.Identifier("GetFunc"),
                    typeParameterList: null,
                    parameterList: parameterList,
                    constraintClauses: SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    body: getFuncBody,
                    semicolonToken: SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                
                .WithAdditionalAnnotations(Formatter.Annotation);

            var members = new List<SyntaxNode>()
            {
                handlerMethod, getFuncMethod
            };
            var classForMethodDefinition = generator.ClassDeclaration(
                "ClassForMethod",
                typeParameters: null,
                accessibility: Accessibility.Public,
                modifiers: DeclarationModifiers.None, 
                members: members);

            var entryProjectAssembly = Assembly.GetAssembly(typeof(T)).GetName().Name;
            var trustedAssembliesPaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator);
            var neededAssemblies = new[]
            {
                "System.Runtime",
                "mscorlib",
                "netstandard",
                "System.Collections",
                entryProjectAssembly 
            };
            var references = trustedAssembliesPaths
                .Where(p => neededAssemblies.Contains(Path.GetFileNameWithoutExtension(p)))
                .Select(p => MetadataReference.CreateFromFile(p))
                .ToList();


            var namespaceDeclaration = generator.NamespaceDeclaration(namespaceName, requestClassDefinition, responseClassDefinition, classForMethodDefinition);

            var result = new MessageTypesContainer()
            {
                NamespaceDeclaration = namespaceDeclaration,
                RequestType = requestClassName,
                ResponseType = responseClassName
            };
            return result;

        }
    }
}

using System.Collections.Generic;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Net.Derid.Interfaces;
using Net.Derid.ProxyGenerator.Model;

namespace Net.Derid.ProxyGenerator
{
    public partial class ProxyGenerator
    {
        private SyntaxNode GetConstructor(SyntaxGenerator generator, TypeStructure typeStructure, EventInfo[] eventInfoForCallSubscriber = null)
        {
            var constructorParameters = new SyntaxNode[]
            {
                SyntaxFactory.Parameter(
                    type: SyntaxFactory.ParseTypeName("Net.Derid.Interfaces.IBusConnector"),
                    identifier: SyntaxFactory.Identifier("bus"),
                    @default: null,
                    attributeLists: new SyntaxList<AttributeListSyntax>(),
                    modifiers: new SyntaxTokenList())
            };

            var line1 = SyntaxFactory.ParseStatement("_bus = bus;");

            List<SyntaxNode>
                constructorBody =
                    new List<SyntaxNode> {line1};

            if(eventInfoForCallSubscriber != null && eventInfoForCallSubscriber.Length > 0) { 
                foreach (var eventInfo in eventInfoForCallSubscriber)
                {
                    constructorBody.Add(SyntaxFactory.ParseStatement(GetEventListenerMethodName(eventInfo)+"();"));
                }
            }

            
            var constructor = generator.ConstructorDeclaration(typeStructure.Name + Constants.ClassNameSuffix,
                constructorParameters, Accessibility.Public,
                statements: constructorBody);

            return constructor;
        }
    }
}

using System.Collections.Generic;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Net.Derid.ProxyGenerator.Model;

namespace Net.Derid.ProxyGenerator
{
    public partial class ProxyGenerator
    {
        public readonly List<string> UsingsList = new List<string>();

        private BlockSyntax GetMethodBody<T>(MethodStructure methodStructure, string requestMessageType, string responseMessageType) where T : class
        {
            var resultType = GetResultTypeOmittingTask(methodStructure.ReturnTypeName);

            var line2 = SyntaxFactory.ParseStatement($"var response = _bus.Request<{requestMessageType}, {responseMessageType}>(\"{typeof(T).Name}_{methodStructure.Name}\", new {requestMessageType}() {{ Method=\"{methodStructure.Name}\", Parameters={GetParamsWithValues(methodStructure)} }});");

            StatementSyntax line3 = SyntaxFactory.ParseStatement("");
            StatementSyntax line4 = SyntaxFactory.ParseStatement("");
            StatementSyntax line5 = SyntaxFactory.ParseStatement("");
            if (methodStructure.ReturnTypeName != "void")
            {
                line3 = SyntaxFactory.ParseStatement(
                    "if (!(response.Result is JToken)) throw new InvalidOperationException(\"Returned data type doesn't match expected return type of JArray\");");
                line4 = SyntaxFactory.ParseStatement(
                    $"var result = ((JToken) response.Result).ToObject<{resultType}>();");
                
                if (methodStructure.ReturnTypeName.StartsWith("System.Threading.Tasks.Task") ||
                    methodStructure.ReturnTypeName.StartsWith("Task"))
                {
                    line5 = SyntaxFactory.ParseStatement("return Task.FromResult(result);");
                }
                else
                {
                    line5 = SyntaxFactory.ParseStatement("return result;");
                }
            }

            var body = SyntaxFactory.Block(
                line2, line3, line4,
                line5);
            return body;
        }
        private string GetParamsWithValues(MethodStructure methodStructure)
        {
            
            var core = string.Empty;
            foreach (var param in methodStructure.Params)
            {
                core += $"new Net.Derid.Interfaces.Models.Parameter() {{Type=\"{param.TypeName}\", Name=\"{param.Name}\",  Value={param.Name} }},";
            }
            var result = $"new List<Net.Derid.Interfaces.Models.Parameter>(){{ {core} }}";
            return result;
        }

        private BlockSyntax GetEventFromBackListener(EventInfo info)
        {
            var line = SyntaxFactory.ParseStatement
            (
                $"_bus.Subscribe<{info.Name}GeneratedMessageNamespace.EventMessageFor{info.Name}>(x => {info.Name}.Invoke(x.EventMessageData)); "
            );

            return SyntaxFactory.Block(line);
        }
    }
}