﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.XPath;
using Autofac;
using Autofac.Core;

namespace Jimu.Server
{
    public class ServiceEntryContainer : IServiceEntryContainer
    {
        private readonly IContainer _container;
        private readonly IServiceIdGenerator _serviceIdGenerate;
        private readonly ITypeConvertProvider _typeConvertProvider;
        private readonly ConcurrentDictionary<Tuple<Type, string>, FastInvoke.FastInvokeHandler> _handler;
        private readonly List<JimuServiceEntry> _services;
        private readonly ISerializer _serializer;
        private const string AllMemberXPath = "/doc/members";
        private const string MemberXPath = "/doc/members/member[@name='{0}']";
        private Dictionary<string, Dictionary<string, XPathNavigator>> _xmlComments;

        public ServiceEntryContainer(IContainer container, IServiceIdGenerator serviceIdGenerate,
                ITypeConvertProvider typeConvertProvider, ISerializer serializer)
        //public ServiceEntryContainer()
        {
            _serviceIdGenerate = serviceIdGenerate;
            _container = container;
            _typeConvertProvider = typeConvertProvider;
            _services = new List<JimuServiceEntry>();
            _handler = new ConcurrentDictionary<Tuple<Type, string>, FastInvoke.FastInvokeHandler>();

            _serializer = serializer;
            _xmlComments = new Dictionary<string, Dictionary<string, XPathNavigator>>();
            //new ConcurrentDictionary<Tuple<Type, string>, object>();
        }


        public IServiceEntryContainer AddServices(Type[] types)
        {
            //var serviceTypes = types.Where(x =>
            //{
            //    var typeinfo = x.GetTypeInfo();
            //    return typeinfo.IsInterface && typeinfo.GetCustomAttribute<JimuServiceRouteAttribute>() != null;
            //}).Distinct();

            var serviceTypes = types
                .Where(x => x.GetMethods().Any(y => y.GetCustomAttribute<JimuServiceAttribute>() != null)).Distinct();

            foreach (var type in serviceTypes)
            {
                var routeTemplate = type.GetCustomAttribute<JimuServiceRouteAttribute>();
                foreach (var methodInfo in type.GetTypeInfo().GetMethods().Where(x => x.GetCustomAttributes<JimuServiceDescAttribute>().Any()))
                {

                    JimuServiceDesc desc = new JimuServiceDesc();
                    var descriptorAttributes = methodInfo.GetCustomAttributes<JimuServiceDescAttribute>();
                    foreach (var attr in descriptorAttributes) attr.Apply(desc);

                    if (string.IsNullOrEmpty(desc.Comment))
                    {
                        var xml = GetXmlComment(methodInfo.DeclaringType);
                        var key = XmlCommentsMemberNameHelper.GetMemberNameForMethod(methodInfo);
                        if (xml != null && xml.TryGetValue(key, out var node))
                        {
                            var summaryNode = node.SelectSingleNode("summary");
                            if (summaryNode != null)
                                desc.Comment = summaryNode.Value.Trim();
                        }
                    }

                    desc.ReturnDesc = GetReturnDesc(methodInfo);

                    if (string.IsNullOrEmpty(desc.HttpMethod))
                        desc.HttpMethod = GetHttpMethod(methodInfo);

                    desc.Parameters = _serializer.Serialize<string>(GetParameters(methodInfo));

                    if (string.IsNullOrEmpty(desc.Id))
                    {
                        desc.Id = _serviceIdGenerate.GenerateServiceId(methodInfo);
                    }

                    var fastInvoker = GetHandler(desc.Id, methodInfo);
                    if (routeTemplate != null)
                        desc.RoutePath = JimuServiceRoute.ParseRoutePath(routeTemplate.RouteTemplate, type.Name,
                            methodInfo.Name, methodInfo.GetParameters(), type.IsInterface);

                    var service = new JimuServiceEntry
                    {
                        Descriptor = desc,
                        Func = (paras, payload) =>
                        {
                            var instance = GetInstance(null, methodInfo.DeclaringType, payload);
                            var parameters = new List<object>();
                            foreach (var para in methodInfo.GetParameters())
                            {
                                paras.TryGetValue(para.Name, out var value);
                                var paraType = para.ParameterType;
                                var parameter = _typeConvertProvider.Convert(value, paraType);
                                parameters.Add(parameter);
                            }

                            var result = fastInvoker(instance, parameters.ToArray());
                            return Task.FromResult(result);
                        }
                    };

                    _services.Add(service);
                }
            }

            return this;
        }

        public List<JimuServiceEntry> GetServiceEntry()
        {
            return _services;
        }

        private FastInvoke.FastInvokeHandler GetHandler(string key, MethodInfo method)
        {
            _handler.TryGetValue(Tuple.Create(method.DeclaringType, key), out var handler);
            if (handler == null)
            {
                handler = FastInvoke.GetMethodInvoker(method);
                _handler.GetOrAdd(Tuple.Create(method.DeclaringType, key), handler);
            }

            return handler;
        }

        private object GetInstance(string key, Type type, JimuPayload payload)
        {
            // all service are instancePerDependency, to avoid resolve the same isntance , so we add using scop here
            using (var scope = _container.BeginLifetimeScope())
            {
                if (string.IsNullOrEmpty(key))
                    return scope.Resolve(type,
                        new ResolvedParameter(
                            (pi, ctx) => pi.ParameterType == typeof(JimuPayload),
                            (pi, ctx) => payload
                        ));
                return scope.ResolveKeyed(key, type,
                    new ResolvedParameter(
                        (pi, ctx) => pi.ParameterType == typeof(JimuPayload),
                        (pi, ctx) => payload
                    ));
            }
        }

        private List<JimuServiceParameterDesc> GetParameters(MethodInfo method)
        {
            //StringBuilder sb = new StringBuilder();
            var paras = new List<JimuServiceParameterDesc>();
            var paraComments = method.GetCustomAttributes<JimuFieldCommentAttribute>();
            var xml = GetXmlComment(method.DeclaringType);
            var key = XmlCommentsMemberNameHelper.GetMemberNameForMethod(method);
            var hasNode = false;
            XPathNavigator node = null;
            if (xml != null)
                hasNode = xml.TryGetValue(key, out node);

            foreach (var para in method.GetParameters())
            {
                var paraDesc = new JimuServiceParameterDesc
                {
                    Name = para.Name,
                    Type = para.ParameterType.ToString(),
                    Comment = paraComments.FirstOrDefault(x => x.FieldName == para.Name)?.Comment,
                };
                if (string.IsNullOrEmpty(paraDesc.Comment) && hasNode)
                {
                    var paraNode = node.SelectSingleNode($"param[@name='{para.Name}']");
                    if (paraNode != null)
                        paraDesc.Comment = paraNode.Value.Trim();
                }
                if (para.ParameterType.IsClass
                && !para.ParameterType.FullName.StartsWith("System."))
                {
                    //var t = Activator.CreateInstance(para.ParameterType);
                    //sb.Append($"\"{para.Name}\":{_serializer.Serialize<string>(t)},");
                    //sb.Append($"\"{para.Name}\":{{{GetCustomTypeMembers(para.ParameterType)}}},");
                    paraDesc.Format = $"{{{ GetCustomTypeMembers(para.ParameterType)}}}";
                }
                else
                {
                    paraDesc.Format = $"{para.ParameterType.ToString()}";
                    //sb.Append($"\"{para.Name}\":\"{para.ParameterType.ToString()}\",");
                }
                paras.Add(paraDesc);
            }
            //return "{" + sb.ToString().TrimEnd(',') + "}";
            return paras;
        }

        private string GetReturnDesc(MethodInfo methodInfo)
        {
            JimuServiceReturnDesc desc = new JimuServiceReturnDesc();
            var jimuReturnComment = methodInfo.GetCustomAttribute<JimuReturnCommentAttribute>();
            if (jimuReturnComment != null)
            {
                desc.Comment = jimuReturnComment.Comment;
                desc.ReturnFormat = jimuReturnComment.Format;
            }
            else
            {
                var xml = GetXmlComment(methodInfo.DeclaringType);
                var key = XmlCommentsMemberNameHelper.GetMemberNameForMethod(methodInfo);
                if (xml != null && xml.TryGetValue(key, out var node))
                {
                    var returnNode = node.SelectSingleNode("returns");
                    desc.Comment = returnNode.Value.Trim();
                }

            }

            List<Type> customTypes = new List<Type>();
            if (methodInfo.ReturnType.ToString().IndexOf("System.Threading.Tasks.Task", StringComparison.Ordinal) == 0 &&
                      methodInfo.ReturnType.IsGenericType)
            {
                //desc.ReturnType = methodInfo.ReturnType.ToString();
                desc.ReturnType = string.Join(",", methodInfo.ReturnType.GenericTypeArguments.Select(x => x.FullName));
                customTypes = (from type in methodInfo.ReturnType.GenericTypeArguments
                               from childType in type.GenericTypeArguments
                               select childType).ToList();
            }
            else if (methodInfo.ReturnType.IsGenericType)
            //if (methodInfo.ReturnType.IsGenericType)
            {
                //desc.ReturnType = string.Join(",", methodInfo.ReturnType.GenericTypeArguments.Select(x => x.FullName));
                desc.ReturnType = methodInfo.ReturnType.ToString();
                customTypes = methodInfo.ReturnType.GenericTypeArguments.ToList();
            }
            else
            {
                desc.ReturnType = methodInfo.ReturnType.ToString();
                customTypes = new List<Type> { methodInfo.ReturnType };
            }

            // if not specify the return format in method attribute, we auto generate it.
            if (string.IsNullOrEmpty(desc.ReturnFormat))
            {
                desc.ReturnFormat = GetReturnFormat(customTypes);
            }
            return _serializer.Serialize<string>(desc);
        }

        private string GetReturnFormat(List<Type> types)
        {
            StringBuilder sb = new StringBuilder();

            foreach (var customType in types)
            {
                if (customType.IsClass
                 && !customType.FullName.StartsWith("System."))
                {
                    sb.Append($"{{{ GetCustomTypeMembers(customType)}}},");
                }
                //else if (customType.FullName.StartsWith("System.Collections.Generic"))
                //{
                //    var childTypes = customType.GenericTypeArguments.ToList();
                //    sb.Append($"[{GetReturnFormat(childTypes)}]");
                //}
                else
                {
                    sb.Append($"{customType.ToString()},");
                }
            }
            return sb.ToString().TrimEnd(',');
        }

        private string GetCustomTypeMembers(Type customType)
        {
            StringBuilder sb = new StringBuilder(); ;
            var xmlNode = GetXmlComment(customType);
            foreach (var prop in customType.GetProperties())
            {
                if (prop.PropertyType.IsClass
              && !prop.PropertyType.FullName.StartsWith("System."))
                {

                    sb.Append($"\"{prop.Name}\":{{{GetCustomTypeMembers(prop.PropertyType)}}}");
                }
                else
                {
                    var comment = prop.GetCustomAttribute<JimuFieldCommentAttribute>();
                    var proComment = comment == null ? "" : (" | " + comment?.Comment);
                    var key = XmlCommentsMemberNameHelper.GetMemberNameForMember(prop);
                    if (comment == null && xmlNode != null && xmlNode.TryGetValue(key, out var node))
                    {
                        proComment = $" | " + node.Value.Trim();
                    }
                    sb.Append($"\"{prop.Name}\":\"{prop.PropertyType.ToString()}{proComment}\",");
                }
            }
            return sb.ToString();
        }

        private string GetHttpMethod(MethodInfo method)
        {
            return method.GetParameters().Any(x => x.ParameterType.IsClass
                && !x.ParameterType.FullName.StartsWith("System.")) ? "POST" : "GET";
        }

        private Dictionary<string, XPathNavigator> GetXmlComment(Type type)
        {
            var key = type.Assembly.GetName().Name;
            //Dictionary<string, XPathNavigator> xmlComments = null;
            if (!_xmlComments.TryGetValue(key, out var xmlComments)
                && File.Exists($"{key}.xml")
                )
            {
                using (var sr = File.OpenText($"{key}.xml"))
                {
                    var xmlMembers = new XPathDocument(sr).CreateNavigator().SelectSingleNode(AllMemberXPath);
                    xmlComments = new Dictionary<string, XPathNavigator>();
                    foreach (XPathNavigator path in xmlMembers.Select("member"))
                    {
                        xmlComments.Add(path.GetAttribute("name", ""), path);
                    }
                    _xmlComments.Add(key, xmlComments);
                }
            }
            return xmlComments;
        }
    }
}