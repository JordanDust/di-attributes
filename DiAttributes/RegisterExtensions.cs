﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DiAttributes
{
    public static class RegisterExtensions
    {
        private const string NullConfigurationException = "A class was decorated with the Configuration attribute " +
            "but RegisterDiAttributes was called without passing in an IConfiguration.";
        private static MethodInfo? configureMethod;

        /// <summary>
        /// Registers all classes that are decorated with the Scoped, Transient, and Singleton attributes.
        /// 
        /// To use the Configuration attribute you need to use the overload which accepts an IConfiguration.
        /// </summary>
        public static void RegisterDiAttributes(this IServiceCollection services)
        {
            Register(services, null);
        }

        /// <summary>
        /// Registers all classes that are decorated with the Scoped, Transient, Singleton, and Configuration attributes.
        /// 
        /// If you're not using the Configuration attribute then you can use the overload which doesn't need an IConfiguration.
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        public static void RegisterDiAttributes(this IServiceCollection services, IConfiguration configuration)
        {
            Register(services, configuration);
        }

        private static void Register(this IServiceCollection services, IConfiguration? configuration)
        {
            if (services == null)
            {
                return;
            }

            IEnumerable<Type> classes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(t => t.GetTypes())
                .Where(t => t.IsClass && t.Name[0] != '<');

            foreach (var @class in classes)
            {
                TryRegisterClass(@class, services, configuration);
            }
        }

        private static void TryRegisterClass(Type @class, IServiceCollection services, IConfiguration? configuration)
        {
            foreach (var customAttribute in @class.CustomAttributes)
            {
                if (customAttribute.AttributeType == typeof(ScopedAttribute))
                {
                    RegisterScopedAttribute(services, @class, customAttribute);
                    return;
                }

                if (customAttribute.AttributeType == typeof(SingletonAttribute))
                {
                    RegisterSingletonAttribute(services, @class, customAttribute);
                    return;
                }

                if (customAttribute.AttributeType == typeof(TransientAttribute))
                {
                    RegisterTransientAttribute(services, @class, customAttribute);
                    return;
                }

                if (customAttribute.AttributeType == typeof(ConfigurationAttribute))
                {
                    if (configuration == null)
                        throw new ArgumentNullException(nameof(configuration), NullConfigurationException);

                    RegisterConfigurationAttribute(services, configuration, @class, customAttribute);
                    return;
                }
            }
        }

        private static void RegisterScopedAttribute(IServiceCollection services, Type injectable, CustomAttributeData customAttributeData)
        {
            if (customAttributeData.AttributeType != typeof(ScopedAttribute))
            {
                string message = $"The given custom attribute needs to have a type of {typeof(ScopedAttribute)}";
                throw new ArgumentException(message, nameof(customAttributeData));
            }

            if (customAttributeData.ConstructorArguments.Count > 0)
            {
                var serviceType = (Type)customAttributeData.ConstructorArguments[0].Value;
                services.AddScoped(serviceType, injectable);
            }
            else
            {
                services.AddScoped(injectable);
            }
        }

        private static void RegisterSingletonAttribute(IServiceCollection services, Type injectable, CustomAttributeData customAttributeData)
        {
            if (customAttributeData.AttributeType != typeof(SingletonAttribute))
            {
                return;
            }

            if (customAttributeData.ConstructorArguments.Count > 0)
            {
                var serviceType = (Type)customAttributeData.ConstructorArguments[0].Value;
                services.AddSingleton(serviceType, injectable);
            }
            else
            {
                services.AddSingleton(injectable);
            }
        }

        private static void RegisterTransientAttribute(IServiceCollection services, Type injectable, CustomAttributeData customAttributeData)
        {
            if (customAttributeData.AttributeType != typeof(TransientAttribute))
            {
                return;
            }

            if (customAttributeData.ConstructorArguments.Count > 0)
            {
                var serviceType = (Type)customAttributeData.ConstructorArguments[0].Value;
                services.AddTransient(serviceType, injectable);
            }
            else
            {
                services.AddTransient(injectable);
            }
        }

        private static void RegisterConfigurationAttribute(IServiceCollection services, IConfiguration config, Type injectable, CustomAttributeData customAttributeData)
        {
            if (customAttributeData.AttributeType != typeof(ConfigurationAttribute))
            {
                return;
            }

            if (customAttributeData.ConstructorArguments.Count <= 0)
            {
                return;
            }

            var key = (string)customAttributeData.ConstructorArguments[0].Value;

            var genericConfigureMethod = GetConfigureMethodForGenericType(injectable);
            try
            {
                genericConfigureMethod.Invoke(services, new object[] { services, config.GetSection(key) });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Unabled to configure the class {injectable.FullName} with the key '{key}'", ex);
            }
        }

        private static MethodInfo GetConfigureMethodForGenericType(Type genericType)
        {
            const BindingFlags StaticMethodBindingFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            if (configureMethod != null)
            {
                return configureMethod.MakeGenericMethod(new Type[] { genericType });
            }

            var allAssemblies = AppDomain.CurrentDomain.GetAssemblies();

            var configurationAssemblies = allAssemblies.Where(a =>
            {
                const string ConfigurationAssemblyName = "Microsoft.Extensions.Options.ConfigurationExtensions,";
                return a.FullName.StartsWith(ConfigurationAssemblyName);
            });

            var allTypes = configurationAssemblies.SelectMany(a => a.GetTypes());

            var sealedNonGenericTypes = allTypes
                .Where(t => t.IsSealed && !t.IsGenericType && !t.IsNested);

            var extensionMethods = sealedNonGenericTypes
                .SelectMany(t => t.GetMethods(StaticMethodBindingFlags))
                .Where(method => method.IsDefined(typeof(ExtensionAttribute), false));

            var extMethodsWithCorrectSignature = extensionMethods.Where<MethodInfo>(method =>
            {
                const string ConfigureMethodName = "Configure";
                if (method.Name != ConfigureMethodName)
                    return false;

                var parameters = method.GetParameters();

                if (parameters.Length != 2)
                    return false;

                if (parameters[0].ParameterType != typeof(IServiceCollection))
                    return false;

                if (parameters[1].ParameterType != typeof(IConfiguration))
                    return false;

                return true;
            });

            if (extMethodsWithCorrectSignature.Count() != 1)
            {
                const string ErrorMessage = "Found more than one IServiceCollection.Configure extension method";
                throw new InvalidOperationException(ErrorMessage);
            }

            var correctExtensionMethod = extMethodsWithCorrectSignature.SingleOrDefault();

            if (correctExtensionMethod == null)
            {
                const string ErrorMessage = "Unable to find the IServiceCollection.Configure extension method";
                throw new InvalidOperationException(ErrorMessage);
            }

            configureMethod = correctExtensionMethod;
            return correctExtensionMethod.MakeGenericMethod(new Type[] { genericType });
        }
    }
}
