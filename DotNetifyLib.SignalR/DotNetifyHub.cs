﻿/* 
Copyright 2016-2017 Dicky Suryadi

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using DotNetify.Security;

namespace DotNetify
{
   /// <summary>
   /// This class is a SignalR hub for communicating with browser clients.
   /// </summary>
   public class DotNetifyHub : Hub
   {
      private readonly IVMControllerFactory _vmControllerFactory;
      private readonly IPrincipalAccessor _principalAccessor;
      private readonly IHubPipeline _hubPipeline;

      private IPrincipal _principal;

      /// <summary>
      /// View model controller associated with the current connection.
      /// </summary>
      private VMController VMController
      {
         get
         {
            if (_principalAccessor is HubPrincipalAccessor)
               (_principalAccessor as HubPrincipalAccessor).Principal = _principal ?? Context.User;

            var vmController = _vmControllerFactory.GetInstance(Context.ConnectionId);
            vmController.RequestingVM = RunRequestingVMFilters;
            vmController.UpdatingVM = RunUpdatingVMFilters;

            return vmController;
         }
      }

      /// <summary>
      /// Constructor for dependency injection.
      /// </summary>
      /// <param name="vmControllerFactory">Factory of view model controllers.</param>
      /// <param name="principalAccessor">Allow to pass the hub principal.</param>
      public DotNetifyHub(IVMControllerFactory vmControllerFactory, IPrincipalAccessor principalAccessor, IHubPipeline hubPipeline)
      {
         _vmControllerFactory = vmControllerFactory;
         _vmControllerFactory.ResponseDelegate = SendResponse;
         _principalAccessor = principalAccessor;
         _hubPipeline = hubPipeline;
      }

      /// <summary>
      /// Handles when a client gets disconnected.
      /// </summary>
      /// <param name="stopCalled">True, if stop was called on the client closing the connection gracefully;
      /// false, if the connection has been lost for longer than the timeout.</param>
      /// <returns></returns>
      public override Task OnDisconnected(bool stopCalled)
      {
         // Remove the controller on disconnection.
         _vmControllerFactory.Remove(Context.ConnectionId);

         // Allow middlewares to hook to the event.
         _hubPipeline.RunDisconnectionMiddlewares(Context);

         return base.OnDisconnected(stopCalled);
      }

      /// <summary>
      /// This method is called by the VMManager to send response back to browser clients.
      /// </summary>
      /// <param name="connectionId">Identifies the browser client making prior request.</param>
      /// <param name="vmId">Identifies the view model.</param>
      /// <param name="vmData">View model data in serialized JSON.</param>
      public void SendResponse(string connectionId, string vmId, string vmData)
      {
         try
         {
            _hubPipeline.RunMiddlewares(Context, nameof(Response_VM), vmId, vmData, ctx =>
            {
               Response_VM(connectionId, ctx.VMId, ctx.Data as string);
               return Task.CompletedTask;
            });
         }
         catch (Exception ex)
         {
            Trace.Fail(ex.ToString());
         }
      }

      #region Client Requests

      /// <summary>
      /// This method is called by browser clients to request view model data.
      /// </summary>
      /// <param name="vmId">Identifies the view model.</param>
      /// <param name="vmArg">Optional argument that may contain view model's initialization argument and/or request headers.</param>
      public void Request_VM(string vmId, object vmArg)
      {
         try
         {
            _hubPipeline.RunMiddlewares(Context, nameof(Request_VM), vmId, vmArg, ctx =>
            {
               _principal = ctx.Principal;
               VMController.OnRequestVM(Context.ConnectionId, ctx.VMId, ctx.Data);
               return Task.CompletedTask;
            });

         }
         catch (Exception ex)
         {
            var finalEx = _hubPipeline.RunExceptionMiddleware(Context, ex);
            Response_VM(Context.ConnectionId, vmId, SerializeException(finalEx));
         }
      }

      /// <summary>
      /// This method is called by browser clients to update a view model's value.
      /// </summary>
      /// <param name="vmId">Identifies the view model.</param>
      /// <param name="vmData">View model update data, where key is the property path and value is the property's new value.</param>
      public void Update_VM(string vmId, Dictionary<string, object> vmData)
      {
         try
         {
            _hubPipeline.RunMiddlewares(Context, nameof(Update_VM), vmId, vmData, ctx =>
            {
               _principal = ctx.Principal;
               VMController.OnUpdateVM(ctx.CallerContext.ConnectionId, ctx.VMId, ctx.Data as Dictionary<string, object>);
               return Task.CompletedTask;
            });
         }
         catch (Exception ex)
         {
            Response_VM(Context.ConnectionId, vmId, SerializeException(ex));
         }
      }

      /// <summary>
      /// This method is called by browser clients to remove its view model as it's no longer used.
      /// </summary>
      /// <param name="vmId">Identifies the view model.  By convention, this should match a view model class name.</param>
      public void Dispose_VM(string vmId)
      {
         try
         {
            VMController.OnDisposeVM(Context.ConnectionId, vmId);
         }
         catch (Exception ex)
         {
            Trace.Fail(ex.ToString());
         }
      }

      #endregion

      #region Server Responses

      /// <summary>
      /// This method is called internally to send response back to browser clients.
      /// </summary>
      /// <param name="connectionId">Identifies the browser client making prior request.</param>
      /// <param name="vmId">Identifies the view model.</param>
      /// <param name="vmData">View model data in serialized JSON.</param>
      internal void Response_VM(string connectionId, string vmId, string vmData)
      {
         if (_vmControllerFactory.GetInstance(connectionId) != null) // Touch the factory to push the timeout.
            Clients.Client(connectionId).Response_VM(vmId, vmData);
      }

      #endregion

      /// <summary>
      /// Runs the view model filter.
      /// </summary>
      /// <param name="vmId">Identifies the view model.</param>
      /// <param name="vm">View model instance.</param>
      /// <param name="vmArg">Optional view model argument.</param>
      private void RunVMFilters(string callType, string vmId, BaseVM vm, ref object vmArg) 
      {
         try
         {
            _hubPipeline.RunVMFilters(Context, callType, vmId, vm, ref vmArg, _principal);
         }
         catch (TargetInvocationException ex)
         {
            throw ex.InnerException;
         }
      }

      /// <summary>
      /// Runs the filter before the view model is requested.
      /// </summary>
      private void RunRequestingVMFilters(string vmId, BaseVM vm, ref object vmArg) => RunVMFilters(nameof(Request_VM), vmId, vm, ref vmArg);

      /// <summary>
      /// Runs the filter before the view model is updated.
      /// </summary>
      private void RunUpdatingVMFilters(string vmId, BaseVM vm, ref object vmArg) => RunVMFilters(nameof(Update_VM), vmId, vm, ref vmArg);

      /// <summary>
      /// Serializes an exception.
      /// </summary>
      /// <param name="ex">Exception to serialize.</param>
      /// <returns>Serialized exception.</returns>
      private string SerializeException(Exception ex) => JsonConvert.SerializeObject(new { ExceptionType = ex.GetType().Name, Message = ex.Message });
   }
}