using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Net.Kluger.Interfaces;
using Net.Kluger.Interfaces.Models;
using Net.Kluger.ProxyGenerator;
using Newtonsoft.Json.Linq;

namespace Net.Kluger.Runtime.Backend
{
    public class ProxyBridge
    {
        public static void SubscribeMethodsToRespond<T, TRequest, TResponse>(IBusConnector bus, object thisObject) where TRequest : Request where TResponse : Response, new()
        {
            var interfaceMethods = typeof(T).GetMethods();
            foreach (var interfaceMethod in interfaceMethods)
            {
                var topic = $"{typeof(T).Name}_{interfaceMethod.Name}";
                var responderFunction = MethodRespond<T, TRequest, TResponse>(thisObject);
                bus.Respond(topic, responderFunction);
            }
        }

        public static void ConnectEventsToProxy(IBusConnector bus, object someService)
        {
            var serviceEvents = someService.GetType().GetEvents();

            foreach (var @event in serviceEvents)
            {
                EventHandlerContainerFabric.BindHandeler(bus, @event, someService);
            }
        }

        public static Func<TRequest, TResponse> MethodRespond<T, TRequest, TResponse>(object thisObject) where TRequest: Request where TResponse: Response, new()
        {
            TResponse ResultFunc(TRequest request)
            {
                var thisType = thisObject.GetType();
                var theMethod = thisType.GetMethod(request.Method, BindingFlags.Instance | BindingFlags.Public);
                var realParams = PrepareParams(request);
                var result = theMethod.Invoke(thisObject, realParams);
                object resultForSerialization = result;

                if (result is Task)
                {
                    var resultProp = result.GetType().GetProperty("Result");
                    if (resultProp != null)
                    {
                        resultForSerialization = resultProp.GetValue(result);
                    }
                }

                return new TResponse
                {
                    Success = true,
                    Result = resultForSerialization,
                };
            }

            return ResultFunc;
        }

        private static object[] PrepareParams(Request request)
        {
            var result = new List<object>();
            foreach (var requestParam in request.Parameters)
            {
                var paramTypeName = requestParam.Type;
                //var temp = Type.ReflectionOnlyGetType("List<>", false, true);
                var paramType = Type.GetType(paramTypeName);
              
                if (paramType == null && paramTypeName.Contains("<")) //means generic
                {
                    var rawType = Regex.Replace(paramTypeName, "<.+>", String.Empty);
                    var innerType = Regex.Match(paramTypeName, ".+<(.+)>").Groups[1].Value;
                    var decodedType = $"{rawType}`1[{innerType}]";
                    paramType = Type.GetType(decodedType);
                }

                var executingAssembly = Assembly.GetExecutingAssembly();
                var referencedAssemblies = executingAssembly.GetReferencedAssemblies();
                var typeAssemblyName = referencedAssemblies.First(x => x.Name == Constants.InterfacesAssemblyName).Name;
                var loadedType = Assembly.Load(typeAssemblyName);
                var definedTypeInAssembly = loadedType.DefinedTypes.FirstOrDefault(x => x.FullName == paramTypeName);
                if (paramType == null && definedTypeInAssembly != null)
                {
                    paramType = definedTypeInAssembly;
                }
                object paramOfRealType;
                if (requestParam.Value is JToken param)
                {
                    var toObjectMethod = typeof(JToken).GetMethods().First(x =>
                        x.Name == "ToObject" && !x.GetParameters().Any() && x.GetGenericArguments().Any());

                    var genericToObjMethod = toObjectMethod.MakeGenericMethod(paramType);
                    paramOfRealType = genericToObjMethod.Invoke(param, null);
                }
                else
                {
                    if (paramType == typeof(Guid))
                    {
                        paramOfRealType = Guid.Parse(requestParam.Value.ToString());
                    }
                    else
                    {
                        paramOfRealType = Convert.ChangeType(requestParam.Value, paramType);
                    }
                }
                result.Add(paramOfRealType);
            }

            return result.ToArray();
        }

        public static void SubscribeMethodUsingDynamicMessages(Net.Kluger.Interfaces.IBusConnector bus, IProxyGenerator proxyGenerator)
        {
            var dynamicAssemblyContainer = proxyGenerator.GenerateClient<ISampleUserService>();
            var requestType = dynamicAssemblyContainer.RequestType;
            var responseType = dynamicAssemblyContainer.ResponseType;
            object delegateObject = null;
            var classForMethod = dynamicAssemblyContainer.DynamicAssembly.DefinedTypes.First(x => x.Name == "ClassForMethod");
            var requestTypeInstance = Activator.CreateInstance(requestType);
            var theMethod = classForMethod.GetMethod("GetFunc", BindingFlags.Public | BindingFlags.Instance);
            var classForMethodInstance = Activator.CreateInstance(classForMethod);
            delegateObject = theMethod.Invoke(classForMethodInstance, new[] { requestTypeInstance });

            var respond = typeof(Net.Kluger.Interfaces.IBusConnector).GetMethod("Respond", BindingFlags.Public | BindingFlags.Instance);
            var respondGeneric = respond.MakeGenericMethod(requestType, responseType);
            var res = respondGeneric.Invoke(bus, new[] { delegateObject });
        }

    }

}